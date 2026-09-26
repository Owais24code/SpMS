using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Commerce.Payments;
using Spms.Modules.Core.Data;
using Spms.Modules.Core.Infrastructure;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Commerce.Application;

public static class Tenders
{
    public const string Card = "Card";
    public const string Cash = "Cash";
    public const string RoomCharge = "RoomCharge";
    public const string GiftCard = "GiftCard";
    public const string MemberAccount = "MemberAccount";
    public const string Deposit = "Deposit";
    public static readonly IReadOnlySet<string> Takeable = new HashSet<string>(StringComparer.Ordinal) { Card, Cash };
}

public static class LineKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { "Service", "Option", "Retail", "Fee", "Deposit", "Discount", "Tip" };
}

public sealed record OrderView(CommerceOrderRow Order, IReadOnlyList<OrderLineRow> Lines, IReadOnlyList<PaymentTransactionRow> Transactions,
    long PaidMinor, long RefundedMinor, long BalanceMinor);

public sealed record NewLine(string LineKind, Guid? AppointmentId, Guid? ServiceId, string? Description, decimal Quantity,
    long? UnitPriceMinor, long DiscountMinor, string? TaxCode);

public sealed record PaymentResult(PaymentIntentRow Intent, PaymentTransactionRow? Transaction, GatewayOutcome Outcome, string? Message);

/// <summary>
/// DEC-001 hybrid ownership: exactly one system owns payment at a property at
/// an instant (the database enforces it). With no explicit row, a Standalone
/// property owns its own; a Marquee-integrated one without a decision is
/// ambiguous, and SpMS refuses to guess (OWNERSHIP_AMBIGUOUS).
/// </summary>
public sealed class PaymentOwnership(SpmsDbContext db)
{
    public const string Capability = "Payment";

    private sealed class OwnerRow { public string OwnerSystem { get; set; } = ""; }

    public async Task<string?> OwnerAsync(CancellationToken ct)
    {
        var property = db.Scope.RequireProperty();
        var explicitOwner = await db.Database.SqlQueryRaw<OwnerRow>(
                "SELECT owner_system AS \"OwnerSystem\" FROM core.capability_ownership " +
                "WHERE property_id = {0} AND capability_code = {1} AND status = 'Active' AND effective_range @> now()",
                property, Capability)
            .ToListAsync(ct);
        if (explicitOwner.Count == 1) return explicitOwner[0].OwnerSystem;
        if (explicitOwner.Count > 1) return null;
        var mode = await db.Set<PropertyRow>().AsNoTracking().Where(p => p.PropertyId == property).Select(p => p.OperatingMode).SingleAsync(ct);
        return mode == "Standalone" ? "Spa" : null;
    }
}

/// <summary>
/// Orders and the money against them. A cart is a Draft order; placing it
/// opens it, applies any deposits taken for its appointments, and it is Paid
/// once approved transactions cover its total (the receipt is issued then).
/// Money is integer minor units throughout. When Marquee owns payment the
/// order is Delegated and the outbound call is recorded for the integration.
/// </summary>
public sealed class CommerceService(
    SpmsDbContext db, IPaymentGateway gateway, PaymentOwnership ownership, SettingsReader settings,
    IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    public enum Outcome { Ok, NotFound, Invalid, Illegal, StaleVersion, Delegated, Ambiguous }

    public sealed record Result<T>(Outcome Outcome, T? Value = default, string? Detail = null);

    private static readonly string[] Money = ["Sale", "Capture", "Adjustment"];

    /* --------------------------------- reads -------------------------------- */

    public async Task<OrderView?> ViewAsync(Guid orderId, CancellationToken ct)
    {
        var o = await db.Set<CommerceOrderRow>().AsNoTracking().SingleOrDefaultAsync(x => x.CommerceOrderId == orderId, ct);
        if (o is null) return null;
        var lines = await db.Set<OrderLineRow>().AsNoTracking().Where(l => l.CommerceOrderId == orderId).OrderBy(l => l.LineNumber).ToListAsync(ct);
        var txs = await db.Set<PaymentTransactionRow>().AsNoTracking().Where(t => t.CommerceOrderId == orderId).OrderBy(t => t.ProcessedAt).ToListAsync(ct);
        var paid = txs.Where(t => t.Outcome == "Approved" && Money.Contains(t.TransactionType)).Sum(t => t.AmountMinor);
        var refunded = txs.Where(t => t.Outcome == "Approved" && t.TransactionType == "Refund").Sum(t => t.AmountMinor);
        // A refund gives money back; it does not put the guest back in debt.
        return new OrderView(o, lines, txs, paid, refunded, Math.Max(0, o.TotalMinor - paid));
    }

    public async Task<List<CommerceOrderRow>> ListAsync(DateOnly? day, string? status, Guid? guestId, CancellationToken ct)
    {
        var q = db.Set<CommerceOrderRow>().AsNoTracking();
        if (status is not null) q = q.Where(o => o.Status == status);
        if (guestId is { } g) q = q.Where(o => o.GuestId == g);
        if (day is { } d)
        {
            var from = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14);
            var to = from.AddHours(38);
            q = q.Where(o => o.CreatedAt >= from && o.CreatedAt < to);
        }
        return await q.OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync(ct);
    }

    /* -------------------------------- orders -------------------------------- */

    public async Task<Result<OrderView>> CreateAsync(Guid? guestId, Guid? visitId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var owner = await ownership.OwnerAsync(ct);
        if (owner is null) return new(Outcome.Ambiguous, Detail: "No single system owns payment at this property. An administrator must decide it.");
        var property = db.Scope.RequireProperty();
        var currency = await db.Set<PropertyRow>().AsNoTracking().Where(p => p.PropertyId == property).Select(p => p.CurrencyCode).SingleAsync(ct);
        var order = new CommerceOrderRow
        {
            CommerceOrderId = Uuid7.New(), PropertyId = property, GuestId = guestId, VisitId = visitId,
            OwnerSystem = owner == "Spa" ? "Spa" : owner is "Marquee" or "Pos" ? owner : "Marquee",
            CurrencyCode = currency.Trim(), Status = owner == "Spa" ? "Draft" : "Delegated",
            ExpiresAt = owner == "Spa" ? clock.UtcNow.AddHours(12) : null,
        };
        db.Add(order);
        await db.SaveChangesAsync(ct);
        if (order.Status == "Delegated")
        {
            // DEC-002: the owning system keeps the cart; SpMS records the call it owes.
            db.Add(new CommerceReferenceRow
            {
                PropertyId = property, CommerceOrderId = order.CommerceOrderId, CapabilityCode = PaymentOwnership.Capability,
                OwnerSystem = owner == "Pos" ? "Pos" : "Marquee", Operation = "CreateCart",
                IdempotencyKey = $"cart:{order.CommerceOrderId:N}", Status = "Pending",
            });
            await db.SaveChangesAsync(ct);
        }
        await Record(order, "commerce.order.create", null, ct);
        var view = await ViewAsync(order.CommerceOrderId, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    public async Task<Result<OrderView>> AddLineAsync(Guid orderId, NewLine n, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var o = await db.Set<CommerceOrderRow>().SingleOrDefaultAsync(x => x.CommerceOrderId == orderId, ct);
        if (o is null) return new(Outcome.NotFound);
        if (o.Status != "Draft") { db.ChangeTracker.Clear(); return new(Outcome.Illegal, Detail: "Lines are added to a cart, before the order is placed."); }

        long unit;
        string description;
        string? taxCode = n.TaxCode;
        Guid? serviceId = n.ServiceId;
        Guid? staffId = null;
        if (n.AppointmentId is { } aid)
        {
            // The price frozen on the booking, not today's catalogue.
            var a = await (from x in db.Set<AppointmentRow>().AsNoTracking()
                           where x.AppointmentId == aid
                           join s in db.Set<ServiceRow>() on x.ServiceId equals s.ServiceId
                           select new { x.PriceMinor, x.ServiceId, x.ProviderId, x.GuestId, x.VisitId, s.Name, s.TaxCode }).SingleOrDefaultAsync(ct);
            if (a is null) { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "No such appointment at this property."); }
            unit = n.UnitPriceMinor ?? a.PriceMinor;
            description = n.Description ?? a.Name;
            taxCode ??= a.TaxCode;
            serviceId ??= a.ServiceId;
            staffId = a.ProviderId;
            // A cart opened from a booking belongs to that booking's guest and visit.
            o.GuestId ??= a.GuestId;
            o.VisitId ??= a.VisitId;
        }
        else if (n.ServiceId is { } sid)
        {
            var s = await db.Set<ServiceRow>().AsNoTracking().Where(x => x.ServiceId == sid).Select(x => new { x.BasePriceMinor, x.Name, x.TaxCode }).SingleOrDefaultAsync(ct);
            if (s is null) { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "Unknown service."); }
            unit = n.UnitPriceMinor ?? s.BasePriceMinor;
            description = n.Description ?? s.Name;
            taxCode ??= s.TaxCode;
        }
        else
        {
            if (n.UnitPriceMinor is null || string.IsNullOrWhiteSpace(n.Description))
            { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "A line without a service needs a description and a unit price."); }
            unit = n.UnitPriceMinor.Value;
            description = n.Description!;
        }
        if (n.LineKind is "Service" or "Option" && serviceId is null) { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "A service line names its service or appointment."); }
        if (n.LineKind == "Retail") { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "Retail lines arrive with inventory (a later release)."); }

        var sign = n.LineKind == "Discount" ? -1 : 1;
        var gross = (long)Math.Round(unit * n.Quantity, MidpointRounding.AwayFromZero) * sign;
        var net = gross - n.DiscountMinor;
        var rate = n.LineKind is "Discount" or "Tip" ? 0m : await TaxRateAsync(taxCode, ct);
        var tax = (long)Math.Round(net * rate, MidpointRounding.AwayFromZero);
        var number = (short)((await db.Set<OrderLineRow>().Where(l => l.CommerceOrderId == orderId).MaxAsync(l => (short?)l.LineNumber, ct) ?? 0) + 1);
        db.Add(new OrderLineRow
        {
            PropertyId = o.PropertyId, CommerceOrderId = orderId, LineNumber = number, LineKind = n.LineKind,
            AppointmentId = n.AppointmentId, ServiceId = serviceId, StaffId = staffId, Description = description,
            Quantity = n.Quantity, UnitPriceMinor = unit * sign, DiscountMinor = n.DiscountMinor, TaxMinor = tax,
            TipMinor = n.LineKind == "Tip" ? gross : 0, NetMinor = net, TaxCode = taxCode,
        });
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(o, ct);
        await db.SaveChangesAsync(ct);
        await Record(o, "commerce.order.line_add", null, ct, new { line = number, n.LineKind, net, tax });
        var view = await ViewAsync(orderId, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    public async Task<Result<OrderView>> RemoveLineAsync(Guid orderId, Guid lineId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var o = await db.Set<CommerceOrderRow>().SingleOrDefaultAsync(x => x.CommerceOrderId == orderId, ct);
        if (o is null) return new(Outcome.NotFound);
        if (o.Status != "Draft") { db.ChangeTracker.Clear(); return new(Outcome.Illegal, Detail: "A placed order's lines are evidence; void or refund instead."); }
        var n = await db.Set<OrderLineRow>().Where(l => l.OrderLineId == lineId && l.CommerceOrderId == orderId).ExecuteDeleteAsync(ct);
        if (n == 0) { db.ChangeTracker.Clear(); return new(Outcome.NotFound); }
        await RecomputeAsync(o, ct);
        await db.SaveChangesAsync(ct);
        await Record(o, "commerce.order.line_remove", null, ct, new { lineId });
        var view = await ViewAsync(orderId, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    /// <summary>Draft → Open: numbered, dated, and any deposits for its appointments applied.</summary>
    public async Task<Result<OrderView>> PlaceAsync(Guid orderId, int expectedVersion, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var o = await db.Set<CommerceOrderRow>().SingleOrDefaultAsync(x => x.CommerceOrderId == orderId, ct);
        if (o is null) return new(Outcome.NotFound);
        if (o.Version != expectedVersion) { db.ChangeTracker.Clear(); return new(Outcome.StaleVersion, await ViewAsync(orderId, ct)); }
        if (o.Status != "Draft") { db.ChangeTracker.Clear(); return new(Outcome.Illegal, Detail: $"A {o.Status} order cannot be placed."); }
        if (!await db.Set<OrderLineRow>().AnyAsync(l => l.CommerceOrderId == orderId, ct)) { db.ChangeTracker.Clear(); return new(Outcome.Invalid, Detail: "An order needs at least one line."); }

        o.Status = "Open";
        o.OrderedAt = clock.UtcNow;
        o.ExpiresAt = null;
        o.OrderNumber = await NumberAsync("ORD", ct, x => x.OrderNumber);
        await db.SaveChangesAsync(ct);

        var appointments = await db.Set<OrderLineRow>().AsNoTracking()
            .Where(l => l.CommerceOrderId == orderId && l.AppointmentId != null).Select(l => l.AppointmentId!.Value).Distinct().ToListAsync(ct);
        foreach (var deposit in await UnappliedDepositsAsync(appointments, ct))
            await SettleDepositAsync(deposit, "DepositApplied", o.CommerceOrderId, ct);

        await RecomputeStatusAsync(o, ct);
        await db.SaveChangesAsync(ct);
        await Record(o, "commerce.order.place", "Draft", ct);
        var view = await ViewAsync(orderId, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    public async Task<Result<OrderView>> VoidAsync(Guid orderId, int expectedVersion, string reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var o = await db.Set<CommerceOrderRow>().SingleOrDefaultAsync(x => x.CommerceOrderId == orderId, ct);
        if (o is null) return new(Outcome.NotFound);
        if (o.Version != expectedVersion) { db.ChangeTracker.Clear(); return new(Outcome.StaleVersion, await ViewAsync(orderId, ct)); }
        var view = await ViewAsync(orderId, ct);
        if (o.Status is not ("Draft" or "Open") || view!.PaidMinor > 0)
        { db.ChangeTracker.Clear(); return new(Outcome.Illegal, Detail: "Only an unpaid order can be voided; refund a paid one."); }
        var from = o.Status;
        o.Status = o.Status == "Draft" ? "Abandoned" : "Voided";
        await db.SaveChangesAsync(ct);
        await Record(o, "commerce.order.void", from, ct, new { reason });
        view = await ViewAsync(orderId, ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    /* ------------------------------- payments ------------------------------- */

    /// <summary>
    /// Takes a payment against an Open order. A card goes through the gateway
    /// under the intent's own key; an Unknown outcome leaves the intent
    /// RequiresAction, and the caller is told to query it, never to pay again.
    /// </summary>
    public async Task<Result<PaymentResult>> PayAsync(Guid orderId, string idempotencyKey, string tender, long? amountMinor,
        string? paymentMethodToken, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        // A retried request is the same payment, whatever the order has become since.
        var existing = await db.Set<PaymentIntentRow>().AsNoTracking().SingleOrDefaultAsync(i => i.IdempotencyKey == $"pay:{idempotencyKey}", ct);
        if (existing is not null)
        {
            if (existing.CommerceOrderId != orderId) return new(Outcome.Invalid, Detail: "This Idempotency-Key was used for another order.");
            var replay = await ReplayAsync(existing, ct);
            return new(replay.Outcome is GatewayOutcome.Unknown or GatewayOutcome.Pending ? Outcome.Ambiguous : Outcome.Ok, replay);
        }
        var view = await ViewAsync(orderId, ct);
        if (view is null) return new(Outcome.NotFound);
        var o = view.Order;
        if (o.Status == "Delegated") return new(Outcome.Delegated, Detail: $"{o.OwnerSystem} owns payment for this order.");
        if (o.Status is not ("Open" or "PartiallyPaid")) return new(Outcome.Illegal, Detail: $"A {o.Status} order does not take payment.");
        var amount = amountMinor ?? view.BalanceMinor;
        if (amount <= 0 || amount > view.BalanceMinor) return new(Outcome.Invalid, Detail: $"The amount must be between 1 and the balance ({view.BalanceMinor}).");
        if (!Tenders.Takeable.Contains(tender)) return new(Outcome.Invalid, Detail: "Card or Cash. Room charges post through the PMS when Marquee is connected.");
        if (tender == Tenders.Card && string.IsNullOrWhiteSpace(paymentMethodToken)) return new(Outcome.Invalid, Detail: "A card payment needs the provider's payment-method token.");


        var intent = new PaymentIntentRow
        {
            PaymentIntentId = Uuid7.New(), PropertyId = o.PropertyId, Purpose = "Payment", CommerceOrderId = orderId,
            AmountMinor = amount, CurrencyCode = o.CurrencyCode, ProviderCode = tender == Tenders.Card ? gateway.ProviderCode : "cash",
            IdempotencyKey = $"pay:{idempotencyKey}", Status = "RequiresPaymentMethod",
        };
        db.Add(intent);
        await db.SaveChangesAsync(ct);
        var result = await ChargeAsync(intent, tender, paymentMethodToken, $"Order {o.OrderNumber}", ct);

        var order = await db.Set<CommerceOrderRow>().SingleAsync(x => x.CommerceOrderId == orderId, ct);
        await RecomputeStatusAsync(order, ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(result.Outcome == GatewayOutcome.Unknown || result.Outcome == GatewayOutcome.Pending ? Outcome.Ambiguous : Outcome.Ok, result);
    }

    /// <summary>
    /// A deposit on a booking (policy.deposit: percent of the frozen price).
    /// Held as a succeeded Deposit intent until the order applies it, or a
    /// no-show forfeits it.
    /// </summary>
    public async Task<Result<PaymentResult>> TakeDepositAsync(Guid appointmentId, string idempotencyKey, long? amountMinor,
        string tender, string? paymentMethodToken, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        if (await ownership.OwnerAsync(ct) is var owner && owner != "Spa")
            return owner is null ? new(Outcome.Ambiguous, Detail: "No single system owns payment here.") : new(Outcome.Delegated, Detail: $"{owner} owns payment at this property.");
        var a = await db.Set<AppointmentRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
        if (a is null) return new(Outcome.NotFound);
        if (a.Status is not ("Held" or "Confirmed")) return new(Outcome.Illegal, Detail: $"A {a.Status} booking does not take a deposit.");
        var existing = await db.Set<PaymentIntentRow>().AsNoTracking().SingleOrDefaultAsync(i => i.IdempotencyKey == $"dep:{idempotencyKey}", ct);
        if (existing is not null) return new(Outcome.Ok, await ReplayAsync(existing, ct));
        if (await db.Set<PaymentIntentRow>().AnyAsync(i => i.AppointmentId == appointmentId && i.Purpose == "Deposit" && i.Status == "Succeeded", ct))
            return new(Outcome.Illegal, Detail: "This booking's deposit is already paid.");
        if (!Tenders.Takeable.Contains(tender) || (tender == Tenders.Card && string.IsNullOrWhiteSpace(paymentMethodToken)))
            return new(Outcome.Invalid, Detail: "Card (with the provider's token) or Cash.");

        var policy = await settings.GetAsync("policy.deposit", new DepositPolicy(), ct);
        var amount = amountMinor ?? Math.Max(policy.MinimumMinor, (long)Math.Round(a.PriceMinor * policy.Percent / 100m, MidpointRounding.AwayFromZero));
        if (amount <= 0 || amount > a.PriceMinor) return new(Outcome.Invalid, Detail: "The deposit must be more than zero and no more than the booking's price.");

        var intent = new PaymentIntentRow
        {
            PaymentIntentId = Uuid7.New(), PropertyId = a.PropertyId, Purpose = "Deposit", AppointmentId = appointmentId,
            AmountMinor = amount, CurrencyCode = a.CurrencyCode.Trim(), ProviderCode = tender == Tenders.Card ? gateway.ProviderCode : "cash",
            IdempotencyKey = $"dep:{idempotencyKey}", Status = "RequiresPaymentMethod",
        };
        db.Add(intent);
        await db.SaveChangesAsync(ct);
        var result = await ChargeAsync(intent, tender, paymentMethodToken, "Deposit", ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(result.Outcome is GatewayOutcome.Unknown or GatewayOutcome.Pending ? Outcome.Ambiguous : Outcome.Ok, result);
    }

    /// <summary>BR-014: an ambiguous outcome is resolved by asking about the original, never by charging again.</summary>
    public async Task<Result<PaymentResult>> ResolveAsync(Guid intentId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var intent = await db.Set<PaymentIntentRow>().SingleOrDefaultAsync(i => i.PaymentIntentId == intentId, ct);
        if (intent is null) return new(Outcome.NotFound);
        if (intent.Status != "RequiresAction") { var replay = await ReplayAsync(intent, ct); db.ChangeTracker.Clear(); return new(Outcome.Ok, replay); }

        var looked = await gateway.LookupAsync(intent.IdempotencyKey, ct);
        if (looked.Outcome is GatewayOutcome.Unknown or GatewayOutcome.Pending)
        {
            db.ChangeTracker.Clear();
            return new(Outcome.Ambiguous, new PaymentResult(intent, null, looked.Outcome, looked.Message));
        }
        var t = await RecordTransactionAsync(intent, Tenders.Card, "Sale", looked, null, ct, suffix: "resolved");
        intent.Status = looked.Outcome == GatewayOutcome.Approved ? "Succeeded" : "Failed";
        intent.ProviderReference ??= looked.ProviderReference;
        await db.SaveChangesAsync(ct);
        if (intent.CommerceOrderId is { } oid)
        {
            var order = await db.Set<CommerceOrderRow>().SingleAsync(o => o.CommerceOrderId == oid, ct);
            await RecomputeStatusAsync(order, ct);
            await db.SaveChangesAsync(ct);
        }
        await audit.RecordAsync(new AuditEntry("commerce.payment.resolve", "payment_intent", intent.PaymentIntentId.ToString(), intent.Version,
            ToStatus: intent.Status, AfterData: new { outcome = looked.Outcome.ToString() }), ct);
        await db.SaveChangesAsync(ct);
        var result = new PaymentResult(intent, t, looked.Outcome, looked.Message);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, result);
    }

    public Task<PaymentIntentRow?> IntentAsync(Guid id, CancellationToken ct) =>
        db.Set<PaymentIntentRow>().AsNoTracking().SingleOrDefaultAsync(i => i.PaymentIntentId == id, ct);

    /* ------------------------------ internals ------------------------------- */

    private async Task<PaymentResult> ChargeAsync(PaymentIntentRow intent, string tender, string? token, string description, CancellationToken ct)
    {
        GatewayResult r = tender == Tenders.Cash
            ? new GatewayResult(GatewayOutcome.Approved, null)
            : await gateway.ChargeAsync(new GatewayCharge(intent.IdempotencyKey, intent.AmountMinor, intent.CurrencyCode, token!, description), ct);
        var t = await RecordTransactionAsync(intent, tender, "Sale", r, null, ct);
        intent.ProviderReference = r.ProviderReference;
        intent.Status = r.Outcome switch
        {
            GatewayOutcome.Approved => "Succeeded",
            GatewayOutcome.Unknown or GatewayOutcome.Pending => "RequiresAction",
            _ => "Failed",
        };
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry($"commerce.{intent.Purpose.ToLowerInvariant()}.take", "payment_intent", intent.PaymentIntentId.ToString(),
            intent.Version, ToStatus: intent.Status,
            AfterData: new { intent.AmountMinor, intent.CurrencyCode, tender, outcome = r.Outcome.ToString(), orderId = intent.CommerceOrderId, appointmentId = intent.AppointmentId }), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.PaymentIntentChanged, "payment_intent", intent.PaymentIntentId, intent.Version,
            new { paymentIntentId = intent.PaymentIntentId, purpose = intent.Purpose, status = intent.Status, amountMinor = intent.AmountMinor,
                  orderId = intent.CommerceOrderId, appointmentId = intent.AppointmentId }));
        await db.SaveChangesAsync(ct);
        return new PaymentResult(intent, t, r.Outcome, r.Message);
    }

    internal async Task<PaymentTransactionRow> RecordTransactionAsync(PaymentIntentRow intent, string tender, string type, GatewayResult r,
        Guid? originalTransactionId, CancellationToken ct, string suffix = "")
    {
        var t = new PaymentTransactionRow
        {
            PaymentTransactionId = Uuid7.New(), PropertyId = intent.PropertyId, PaymentIntentId = intent.PaymentIntentId,
            CommerceOrderId = intent.CommerceOrderId, TenderCode = tender, TransactionType = type,
            Outcome = r.Outcome.ToString(), AmountMinor = intent.AmountMinor, CurrencyCode = intent.CurrencyCode,
            ProviderCode = intent.ProviderCode, ProviderReference = r.ProviderReference, CardBrand = r.CardBrand, CardLast4 = r.Last4,
            ProcessedAt = clock.UtcNow, OriginalTransactionId = originalTransactionId,
            IdempotencyKey = $"tx:{intent.PaymentIntentId:N}:{type}{(suffix.Length > 0 ? ":" + suffix : "")}",
        };
        db.Add(t);
        await db.SaveChangesAsync(ct);
        return t;
    }

    private async Task<PaymentResult> ReplayAsync(PaymentIntentRow intent, CancellationToken ct)
    {
        var t = await db.Set<PaymentTransactionRow>().AsNoTracking().Where(x => x.PaymentIntentId == intent.PaymentIntentId)
            .OrderByDescending(x => x.ProcessedAt).FirstOrDefaultAsync(ct);
        var outcome = Enum.TryParse<GatewayOutcome>(t?.Outcome, out var o) ? o : GatewayOutcome.Unknown;
        return new PaymentResult(intent, t, outcome, null);
    }

    private async Task<List<PaymentIntentRow>> UnappliedDepositsAsync(IReadOnlyCollection<Guid> appointments, CancellationToken ct)
    {
        if (appointments.Count == 0) return [];
        var deposits = await db.Set<PaymentIntentRow>().Where(i => i.AppointmentId != null && appointments.Contains(i.AppointmentId.Value)
            && i.Purpose == "Deposit" && i.Status == "Succeeded").ToListAsync(ct);
        var settled = await db.Set<PaymentIntentRow>().AsNoTracking()
            .Where(i => i.AppointmentId != null && appointments.Contains(i.AppointmentId.Value) && (i.Purpose == "DepositApplied" || i.Purpose == "DepositForfeited"))
            .Select(i => i.AppointmentId).ToListAsync(ct);
        return deposits.Where(d => !settled.Contains(d.AppointmentId)).ToList();
    }

    /// <summary>A deposit is applied to an order, or forfeited on a no-show: a settled intent and an adjustment, once.</summary>
    internal async Task SettleDepositAsync(PaymentIntentRow deposit, string purpose, Guid? orderId, CancellationToken ct)
    {
        var settle = new PaymentIntentRow
        {
            PaymentIntentId = Uuid7.New(), PropertyId = deposit.PropertyId, Purpose = purpose, AppointmentId = deposit.AppointmentId,
            CommerceOrderId = orderId, AmountMinor = deposit.AmountMinor, CurrencyCode = deposit.CurrencyCode,
            ProviderCode = "internal", IdempotencyKey = $"{purpose}:{deposit.PaymentIntentId:N}", Status = "Succeeded",
        };
        db.Add(settle);
        await db.SaveChangesAsync(ct);
        await RecordTransactionAsync(settle, Tenders.Deposit, "Adjustment", new GatewayResult(GatewayOutcome.Approved, null), null, ct);
        await audit.RecordAsync(new AuditEntry($"commerce.deposit.{(purpose == "DepositApplied" ? "apply" : "forfeit")}", "payment_intent",
            deposit.PaymentIntentId.ToString(), deposit.Version, AfterData: new { amount = deposit.AmountMinor, orderId, appointmentId = deposit.AppointmentId }), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task RecomputeAsync(CommerceOrderRow o, CancellationToken ct)
    {
        var lines = await db.Set<OrderLineRow>().AsNoTracking().Where(l => l.CommerceOrderId == o.CommerceOrderId).ToListAsync(ct);
        o.SubtotalMinor = lines.Where(l => l.LineKind is not ("Discount" or "Tip")).Sum(l => l.NetMinor);
        o.DiscountTotalMinor = -lines.Where(l => l.LineKind == "Discount").Sum(l => l.NetMinor) + lines.Where(l => l.LineKind != "Discount").Sum(l => l.DiscountMinor);
        o.TaxTotalMinor = lines.Sum(l => l.TaxMinor);
        o.TipTotalMinor = lines.Sum(l => l.TipMinor);
        o.TotalMinor = lines.Sum(l => l.NetMinor) + o.TaxTotalMinor;
    }

    internal async Task RecomputeStatusAsync(CommerceOrderRow o, CancellationToken ct)
    {
        if (o.Status is "Draft" or "Delegated" or "Voided" or "Abandoned") return;
        var txs = await db.Set<PaymentTransactionRow>().AsNoTracking().Where(t => t.CommerceOrderId == o.CommerceOrderId && t.Outcome == "Approved").ToListAsync(ct);
        var paid = txs.Where(t => Money.Contains(t.TransactionType)).Sum(t => t.AmountMinor);
        var refunded = txs.Where(t => t.TransactionType == "Refund").Sum(t => t.AmountMinor);
        var previous = o.Status;
        o.Status = refunded > 0 && refunded >= paid ? "Refunded"
            : refunded > 0 ? "PartiallyRefunded"
            : paid >= o.TotalMinor && o.TotalMinor > 0 ? "Paid"
            : paid > 0 ? "PartiallyPaid" : "Open";
        if (o.Status == "Paid" && o.ReceiptNumber is null)
        {
            o.ReceiptNumber = await NumberAsync("RCT", ct, x => x.ReceiptNumber);
            o.ReceiptIssuedAt = clock.UtcNow;
        }
        if (o.Status != previous)
            outbox.Enqueue(new OutboxEvent(EventTypes.OrderChanged, "commerce_order", o.CommerceOrderId, o.Version + 1,
                new { orderId = o.CommerceOrderId, from = previous, to = o.Status, total = o.TotalMinor, paid, refunded }));
    }

    private async Task<decimal> TaxRateAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return 0m;
        var now = clock.UtcNow;
        var property = db.Scope.CurrentPropertyId;
        var rates = await db.Set<TaxRuleRow>().AsNoTracking()
            .Where(t => t.TaxCode == code && t.Status == TaxRuleStatuses.Active && t.EffectiveFrom <= now && (t.EffectiveTo == null || t.EffectiveTo > now)
                        && (t.PropertyId == null || t.PropertyId == property))
            .OrderByDescending(t => t.PropertyId != null).Select(t => t.Rate).Take(1).ToListAsync(ct);
        return rates.Count == 0 ? 0m : rates[0];
    }

    private async Task<string> NumberAsync(string prefix, CancellationToken ct, System.Linq.Expressions.Expression<Func<CommerceOrderRow, string?>> column)
    {
        for (var i = 0; i < 8; i++)
        {
            var n = prefix + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString("D9");
            var param = column.Parameters[0];
            var eq = System.Linq.Expressions.Expression.Lambda<Func<CommerceOrderRow, bool>>(
                System.Linq.Expressions.Expression.Equal(column.Body, System.Linq.Expressions.Expression.Constant(n, typeof(string))), param);
            if (!await db.Set<CommerceOrderRow>().AsNoTracking().AnyAsync(eq, ct)) return n;
        }
        throw new InvalidOperationException($"Could not allocate a {prefix} number.");
    }

    private async Task Record(CommerceOrderRow o, string action, string? from, CancellationToken ct, object? data = null)
    {
        await audit.RecordAsync(new AuditEntry(action, "commerce_order", o.CommerceOrderId.ToString(), o.Version,
            FromStatus: from, ToStatus: o.Status, AfterData: data ?? new { o.TotalMinor, o.OwnerSystem }), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.OrderChanged, "commerce_order", o.CommerceOrderId, o.Version,
            new { orderId = o.CommerceOrderId, action, status = o.Status, total = o.TotalMinor, owner = o.OwnerSystem }));
        await db.SaveChangesAsync(ct);
    }
}

public sealed record DepositPolicy(decimal Percent = 50m, long MinimumMinor = 0);
