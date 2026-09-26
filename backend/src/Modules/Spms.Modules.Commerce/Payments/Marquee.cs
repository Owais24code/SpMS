using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Commerce.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Commerce.Payments;

public sealed record MarqueeCall(Guid ReferenceId, string Operation, Guid? OrderId, long? AmountMinor, string? Currency, string IdempotencyKey);
public sealed record MarqueeAnswer(bool Accepted, string? ExternalId, string? Error = null);

/// <summary>
/// The Marquee commerce API, behind a port (DEC-002). Every call carries the
/// reference's own idempotency key, so a resend after a timeout is the same
/// call to Marquee.
/// </summary>
public interface IMarqueeClient
{
    Task<MarqueeAnswer> SendAsync(MarqueeCall call, CancellationToken ct);
}

/// <summary>The development stand-in: accepts every call once per key and names the cart it "opened".</summary>
public sealed class SimulatedMarquee : IMarqueeClient
{
    private readonly ConcurrentDictionary<string, MarqueeAnswer> _byKey = new(StringComparer.Ordinal);

    public Task<MarqueeAnswer> SendAsync(MarqueeCall call, CancellationToken ct) =>
        Task.FromResult(_byKey.GetOrAdd(call.IdempotencyKey, _ => new MarqueeAnswer(true, $"MQ-{call.Operation.ToLowerInvariant()}-{Guid.NewGuid():N}"[..28])));
}

/// <summary>
/// Sends the calls SpMS owes the system that owns commerce (DEC-001/002):
/// each Pending reference once, by its key. A failure keeps it Pending with
/// the error recorded, and the next run tries again with the same key.
/// </summary>
public sealed class MarqueeOutboundJob(SpmsDbContext db, IMarqueeClient marquee, IAuditSink audit) : IPropertyJob
{
    public string Name => "commerce.marquee-outbound";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var pending = await db.Set<CommerceReferenceRow>().Where(r => r.Status == "Pending" && r.OwnerSystem == "Marquee").OrderBy(r => r.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var r in pending)
        {
            MarqueeAnswer answer;
            try { answer = await marquee.SendAsync(new MarqueeCall(r.ReferenceId, r.Operation, r.CommerceOrderId, r.AmountMinor, r.CurrencyCode, r.IdempotencyKey), ct); }
            catch (HttpRequestException e) { answer = new MarqueeAnswer(false, null, e.Message); }
            if (answer.Accepted) { r.Status = "Sent"; r.ExternalId = answer.ExternalId; r.LastError = null; }
            else r.LastError = answer.Error ?? "Refused";
            await audit.RecordAsync(new AuditEntry("commerce.marquee.send", "commerce_reference", r.ReferenceId.ToString(), r.Version + 1,
                ToStatus: r.Status, ReasonText: r.LastError, AfterData: new { r.Operation, r.ExternalId }), ct);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return pending.Count;
    }
}

/// <summary>
/// What Marquee tells SpMS about a cart it owns: settled (the guest paid at
/// Marquee; the order is Paid with Marquee's receipt) or voided. The order is
/// found by the cart reference SpMS recorded when it opened the cart.
/// </summary>
public sealed class MarqueeCommerceHandler(SpmsDbContext db, IAuditSink audit, IOutbox outbox, IClock clock) : IInboundHandler
{
    public IReadOnlyCollection<string> EventTypes { get; } = ["marquee.cart.settled", "marquee.cart.voided"];

    public async Task<string> HandleAsync(string sourceSystem, string eventType, JsonElement payload, CancellationToken ct)
    {
        if (sourceSystem != "marquee") throw new InboundRejected("Only Marquee settles Marquee carts.");
        var cart = payload.TryGetProperty("cartReference", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(cart)) throw new InboundRejected("cartReference is required.");
        var reference = await db.Set<CommerceReferenceRow>().SingleOrDefaultAsync(r => r.ExternalId == cart && r.Operation == "CreateCart", ct)
            ?? throw new InboundRejected("No cart with that reference at this property.");
        var order = await db.Set<CommerceOrderRow>().SingleAsync(o => o.CommerceOrderId == reference.CommerceOrderId, ct);
        if (order.Status != "Delegated") return $"Already {order.Status}.";
        var from = order.Status;
        if (eventType == "marquee.cart.settled")
        {
            if (!payload.TryGetProperty("totalMinor", out var t) || !t.TryGetInt64(out var total) || total < 0) throw new InboundRejected("totalMinor is required.");
            order.TotalMinor = total;
            order.ReceiptNumber = payload.TryGetProperty("receiptNumber", out var rn) ? rn.GetString() : null;
            order.ReceiptIssuedAt = order.ReceiptNumber is null ? null : clock.UtcNow;
            order.Status = "Paid";
            reference.Status = "Confirmed";
            reference.AmountMinor = total;
        }
        else
        {
            order.Status = "Voided";
            reference.Status = "Confirmed";
        }
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry($"commerce.marquee.{(order.Status == "Paid" ? "settled" : "voided")}", "commerce_order", order.CommerceOrderId.ToString(),
            order.Version, FromStatus: from, ToStatus: order.Status, AfterData: new { cart, order.TotalMinor, order.ReceiptNumber }), ct);
        outbox.Enqueue(new OutboxEvent(Spms.SharedKernel.EventTypes.OrderChanged, "commerce_order", order.CommerceOrderId, order.Version,
            new { orderId = order.CommerceOrderId, action = eventType, status = order.Status, total = order.TotalMinor, owner = order.OwnerSystem }));
        await db.SaveChangesAsync(ct);
        return $"Order {order.CommerceOrderId} {order.Status}.";
    }
}
