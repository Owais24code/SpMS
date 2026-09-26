using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Commerce.Payments;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Commerce.Application;

/// <summary>
/// Refunds under dual control (commerce:refund:approved, SEC-014): one person
/// requests, a different person with the refund right approves, and only then
/// does money move. The database refuses an approver who is the requester, and
/// no refund may exceed what is left of the original.
/// </summary>
public sealed class RefundService(SpmsDbContext db, CommerceService commerce, IPaymentGateway gateway, IAuditSink audit, IOutbox outbox,
    IUnitOfWork uow, IClock clock)
{
    public enum Outcome { Ok, NotFound, Invalid, Illegal, SameApprover, Failed }

    public sealed record Result(Outcome Outcome, PaymentIntentRow? Intent = null, PaymentTransactionRow? Transaction = null, string? Detail = null);

    public async Task<long> RefundableAsync(Guid transactionId, CancellationToken ct)
    {
        var original = await db.Set<PaymentTransactionRow>().AsNoTracking().SingleOrDefaultAsync(t => t.PaymentTransactionId == transactionId, ct);
        if (original is null) return 0;
        var refunded = await db.Set<PaymentTransactionRow>().AsNoTracking()
            .Where(t => t.OriginalTransactionId == transactionId && t.TransactionType == "Refund" && t.Outcome == "Approved").SumAsync(t => t.AmountMinor, ct);
        var pending = await db.Set<PaymentIntentRow>().AsNoTracking()
            .Where(i => i.OriginalTransactionId == transactionId && i.Purpose == "Refund" && (i.Status == "Requested" || i.Status == "Approved"))
            .SumAsync(i => i.AmountMinor, ct);
        return Math.Max(0, original.AmountMinor - refunded - pending);
    }

    public async Task<Result> RequestAsync(Guid transactionId, long amountMinor, string reasonCode, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var original = await db.Set<PaymentTransactionRow>().AsNoTracking().SingleOrDefaultAsync(t => t.PaymentTransactionId == transactionId, ct);
        if (original is null) return new(Outcome.NotFound);
        if (original.Outcome != "Approved" || original.TransactionType is not ("Sale" or "Capture"))
            return new(Outcome.Illegal, Detail: "Only an approved sale can be refunded.");
        var refundable = await RefundableAsync(transactionId, ct);
        if (amountMinor <= 0 || amountMinor > refundable)
            return new(Outcome.Invalid, Detail: $"The refund must be between 1 and what is left of the original ({refundable}).");
        var intent = new PaymentIntentRow
        {
            PaymentIntentId = Uuid7.New(), PropertyId = original.PropertyId, Purpose = "Refund",
            CommerceOrderId = original.CommerceOrderId,
            AppointmentId = original.CommerceOrderId is null
                ? await db.Set<PaymentIntentRow>().Where(i => i.PaymentIntentId == original.PaymentIntentId).Select(i => i.AppointmentId).SingleOrDefaultAsync(ct)
                : null,
            OriginalTransactionId = transactionId, AmountMinor = amountMinor, CurrencyCode = original.CurrencyCode,
            ProviderCode = original.ProviderCode, ReasonCode = reasonCode, IdempotencyKey = $"refund:{Uuid7.New():N}", Status = "Requested",
        };
        db.Add(intent);
        await db.SaveChangesAsync(ct);
        await Publish(intent, "commerce.refund.request", null, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, intent);
    }

    public async Task<Result> ApproveAsync(Guid intentId, int expectedVersion, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var intent = await db.Set<PaymentIntentRow>().SingleOrDefaultAsync(i => i.PaymentIntentId == intentId && i.Purpose == "Refund", ct);
        if (intent is null) return new(Outcome.NotFound);
        if (intent.Version != expectedVersion || intent.Status != "Requested") { db.ChangeTracker.Clear(); return new(Outcome.Illegal, intent, Detail: $"A {intent.Status} refund cannot be approved."); }
        if (intent.CreatedBy == db.Scope.PrincipalId) { db.ChangeTracker.Clear(); return new(Outcome.SameApprover, Detail: "The person who requested a refund cannot approve it."); }

        var original = await db.Set<PaymentTransactionRow>().AsNoTracking().SingleAsync(t => t.PaymentTransactionId == intent.OriginalTransactionId, ct);
        intent.Status = "Approved";
        intent.ApprovedBy = db.Scope.PrincipalId;
        intent.ApprovedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var r = original.TenderCode == Tenders.Card
            ? await gateway.RefundAsync(new GatewayRefund(intent.IdempotencyKey, original.ProviderReference ?? "", intent.AmountMinor, intent.CurrencyCode), ct)
            : new GatewayResult(GatewayOutcome.Approved, null);
        var t = await commerce.RecordTransactionAsync(intent, original.TenderCode, "Refund", r, original.PaymentTransactionId, ct);
        intent.Status = r.Outcome == GatewayOutcome.Approved ? "Succeeded" : r.Outcome is GatewayOutcome.Unknown or GatewayOutcome.Pending ? "RequiresAction" : "Failed";
        intent.ProviderReference = r.ProviderReference;
        await db.SaveChangesAsync(ct);
        if (intent.CommerceOrderId is { } oid)
        {
            var order = await db.Set<CommerceOrderRow>().SingleAsync(o => o.CommerceOrderId == oid, ct);
            await commerce.RecomputeStatusAsync(order, ct);
            await db.SaveChangesAsync(ct);
        }
        await Publish(intent, "commerce.refund.approve", "Requested", ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(intent.Status == "Failed" ? Outcome.Failed : Outcome.Ok, intent, t);
    }

    public async Task<Result> RejectAsync(Guid intentId, int expectedVersion, string reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var intent = await db.Set<PaymentIntentRow>().SingleOrDefaultAsync(i => i.PaymentIntentId == intentId && i.Purpose == "Refund", ct);
        if (intent is null) return new(Outcome.NotFound);
        if (intent.Version != expectedVersion || intent.Status != "Requested") { db.ChangeTracker.Clear(); return new(Outcome.Illegal, intent, Detail: $"A {intent.Status} refund cannot be rejected."); }
        intent.Status = "Rejected";
        intent.ReasonCode = intent.ReasonCode is null ? reason : $"{intent.ReasonCode}; rejected: {reason}";
        await db.SaveChangesAsync(ct);
        await Publish(intent, "commerce.refund.reject", "Requested", ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, intent);
    }

    private async Task Publish(PaymentIntentRow i, string action, string? from, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "payment_intent", i.PaymentIntentId.ToString(), i.Version, FromStatus: from, ToStatus: i.Status,
            ReasonCode: i.ReasonCode, AfterData: new { i.AmountMinor, i.CurrencyCode, original = i.OriginalTransactionId, orderId = i.CommerceOrderId }), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.PaymentIntentChanged, "payment_intent", i.PaymentIntentId, i.Version,
            new { paymentIntentId = i.PaymentIntentId, purpose = i.Purpose, status = i.Status, amountMinor = i.AmountMinor, orderId = i.CommerceOrderId }));
        await db.SaveChangesAsync(ct);
    }
}

public sealed record TenderTotal(string TenderCode, string TransactionType, int Count, long AmountMinor);

public sealed record Reconciliation(DateOnly Day, IReadOnlyList<TenderTotal> Totals, IReadOnlyList<PaymentIntentRow> Ambiguous,
    IReadOnlyList<PaymentIntentRow> RefundsAwaitingApproval, IReadOnlyList<PaymentTransactionRow> Transactions, long NetMinor);

/// <summary>The day's money for finance: totals by tender and type, ambiguous outcomes to resolve, refunds waiting for a second person.</summary>
public sealed class ReconciliationService(SpmsDbContext db, CommerceService commerce)
{
    public async Task<Reconciliation> DayAsync(DateOnly day, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var txs = await db.Set<PaymentTransactionRow>().AsNoTracking()
            .Where(t => t.ProcessedAt >= fromUtc && t.ProcessedAt < toUtc).OrderBy(t => t.ProcessedAt).ToListAsync(ct);
        var totals = txs.Where(t => t.Outcome == "Approved").GroupBy(t => (t.TenderCode, t.TransactionType))
            .Select(g => new TenderTotal(g.Key.TenderCode, g.Key.TransactionType, g.Count(), g.Sum(t => t.AmountMinor)))
            .OrderBy(t => t.TenderCode).ThenBy(t => t.TransactionType).ToList();
        var ambiguous = await db.Set<PaymentIntentRow>().AsNoTracking().Where(i => i.Status == "RequiresAction").OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var refunds = await db.Set<PaymentIntentRow>().AsNoTracking().Where(i => i.Purpose == "Refund" && i.Status == "Requested").OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var net = txs.Where(t => t.Outcome == "Approved" && t.TenderCode != Tenders.Deposit)
            .Sum(t => t.TransactionType is "Sale" or "Capture" ? t.AmountMinor : t.TransactionType == "Refund" ? -t.AmountMinor : 0);
        return new Reconciliation(day, totals, ambiguous, refunds, txs, net);
    }

    /// <summary>Asks the provider about every ambiguous outcome; returns how many are now settled.</summary>
    public async Task<int> ResolveAllAsync(CancellationToken ct)
    {
        var ids = await db.Set<PaymentIntentRow>().AsNoTracking().Where(i => i.Status == "RequiresAction").Select(i => i.PaymentIntentId).Take(100).ToListAsync(ct);
        var settled = 0;
        foreach (var id in ids)
            if ((await commerce.ResolveAsync(id, ct)).Outcome == CommerceService.Outcome.Ok) settled++;
        return settled;
    }
}

/// <summary>The job that settles ambiguous payment outcomes by lookup, so none waits for someone to press a button.</summary>
public sealed class AmbiguousPaymentJob(ReconciliationService reconciliation) : IPropertyJob
{
    public string Name => "commerce.ambiguous-payments";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public Task<int> RunAsync(CancellationToken ct) => reconciliation.ResolveAllAsync(ct);
}

/// <summary>Expired carts become Abandoned.</summary>
public sealed class CartExpiryJob(SpmsDbContext db, IClock clock) : IPropertyJob
{
    public string Name => "commerce.cart-expiry";
    public TimeSpan Interval => TimeSpan.FromMinutes(15);
    public Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        return db.Set<CommerceOrderRow>().Where(o => o.Status == "Draft" && o.ExpiresAt <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, "Abandoned").SetProperty(o => o.Version, o => o.Version + 1), ct);
    }
}

/// <summary>A no-show keeps its deposit (policy): the deposit is forfeited in the same transaction as the no-show.</summary>
public sealed class DepositForfeitObserver(SpmsDbContext db, CommerceService commerce) : ISchedulingObserver
{
    public async Task TransitionedAsync(Appointment after, AppointmentStatus from, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        if (after.Status != AppointmentStatus.NoShow || !Guid.TryParse(after.AppointmentId, out var id)) return;
        var deposits = await db.Set<PaymentIntentRow>().Where(i => i.AppointmentId == id && i.Purpose == "Deposit" && i.Status == "Succeeded").ToListAsync(ct);
        var settled = await db.Set<PaymentIntentRow>().AnyAsync(i => i.AppointmentId == id && (i.Purpose == "DepositApplied" || i.Purpose == "DepositForfeited"), ct);
        if (settled) return;
        foreach (var d in deposits) await commerce.SettleDepositAsync(d, "DepositForfeited", null, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
    }
}

/// <summary>Deposit readiness for the desk's arrivals list.</summary>
public sealed class DepositStatus(SpmsDbContext db) : IDepositStatus
{
    public async Task<IReadOnlyDictionary<Guid, string>> ForAppointmentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        var required = await (from a in db.Set<AppointmentRow>().AsNoTracking()
                              where ids.Contains(a.AppointmentId)
                              join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
                              select new { a.AppointmentId, s.DepositRequired }).ToListAsync(ct);
        var paid = (await db.Set<PaymentIntentRow>().AsNoTracking()
            .Where(i => i.AppointmentId != null && ids.Contains(i.AppointmentId.Value) && i.Purpose == "Deposit" && i.Status == "Succeeded")
            .Select(i => i.AppointmentId!.Value).ToListAsync(ct)).ToHashSet();
        return required.ToDictionary(r => r.AppointmentId, r => paid.Contains(r.AppointmentId) ? "Settled" : r.DepositRequired ? "Pending" : "NotRequired");
    }
}

/// <summary>Commerce's part of a privacy request: orders and payments are financial records, exported and kept.</summary>
public sealed class CommerceGuestData(SpmsDbContext db) : IGuestDataContributor
{
    public string Section => "commerce";

    public async Task<object?> ExportAsync(Guid guestId, CancellationToken ct)
    {
        var orders = await db.Set<CommerceOrderRow>().AsNoTracking().Where(o => o.GuestId == guestId)
            .Select(o => new { o.CommerceOrderId, o.OrderNumber, o.ReceiptNumber, o.Status, o.TotalMinor, o.CurrencyCode, o.OrderedAt }).ToListAsync(ct);
        return new { orders };
    }

    public Task<int> EraseAsync(Guid guestId, CancellationToken ct) => Task.FromResult(0);
}
