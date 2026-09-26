using System.Text.Json;

namespace Spms.SharedKernel;

/// <summary>
/// An event from a connected system (Marquee, a PMS, a POS), consumed once:
/// the integration endpoint records (consumer, event id) in core.event_inbox
/// in the same transaction as the handler's work, so a redelivery is skipped.
/// </summary>
public interface IInboundHandler
{
    /// <summary>The event types this handler takes, e.g. "marquee.cart.settled".</summary>
    IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>Returns a short outcome for the caller, or throws InboundRejected for a payload it refuses.</summary>
    Task<string> HandleAsync(string sourceSystem, string eventType, JsonElement payload, CancellationToken ct);
}

public sealed class InboundRejected(string reason) : Exception(reason);
