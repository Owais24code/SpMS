using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Spms.Modules.Commerce.Payments;

public enum GatewayOutcome { Approved, Declined, Error, Pending, Unknown }

public sealed record GatewayCharge(string IdempotencyKey, long AmountMinor, string Currency, string PaymentMethodToken, string Description);

public sealed record GatewayRefund(string IdempotencyKey, string OriginalReference, long AmountMinor, string Currency);

public sealed record GatewayResult(GatewayOutcome Outcome, string? ProviderReference, string? CardBrand = null, string? Last4 = null, string? Message = null);

/// <summary>
/// The payment provider, behind a port (DEC-010 is open: the provider is not
/// chosen). Every call carries the caller's idempotency key, so a retried
/// request is the same request to the provider. An Unknown outcome is never
/// retried blind (BR-014): the original is looked up by its key.
/// Card data never reaches SpMS — only the provider's token for it.
/// </summary>
public interface IPaymentGateway
{
    string ProviderCode { get; }
    Task<GatewayResult> ChargeAsync(GatewayCharge charge, CancellationToken ct);
    Task<GatewayResult> RefundAsync(GatewayRefund refund, CancellationToken ct);
    /// <summary>What happened to the request with this key; Unknown if the provider still cannot say.</summary>
    Task<GatewayResult> LookupAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// The development and test provider. Deterministic by token:
///   tok_approve / tok_visa_4242   approved
///   tok_decline                   declined
///   tok_error                     provider error
///   tok_timeout                   the outcome is unknown at first; a lookup finds it approved
/// A key seen before returns the first result, as a real provider's idempotency does.
/// </summary>
public sealed class SimulatedGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, GatewayResult> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GatewayResult> _settled = new(StringComparer.Ordinal);

    public string ProviderCode => "sim";

    public Task<GatewayResult> ChargeAsync(GatewayCharge c, CancellationToken ct) =>
        Task.FromResult(_byKey.GetOrAdd(c.IdempotencyKey, _ =>
        {
            var reference = "sim_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            return c.PaymentMethodToken switch
            {
                "tok_decline" => new GatewayResult(GatewayOutcome.Declined, reference, "Visa", "0002", "Card declined"),
                "tok_error" => new GatewayResult(GatewayOutcome.Error, null, Message: "Provider unavailable"),
                "tok_timeout" => Remember(c.IdempotencyKey, new GatewayResult(GatewayOutcome.Unknown, null, Message: "No response from provider"),
                    new GatewayResult(GatewayOutcome.Approved, reference, "Visa", "4242")),
                _ => new GatewayResult(GatewayOutcome.Approved, reference, "Visa", "4242"),
            };
        }));

    public Task<GatewayResult> RefundAsync(GatewayRefund r, CancellationToken ct) =>
        Task.FromResult(_byKey.GetOrAdd(r.IdempotencyKey, _ =>
            new GatewayResult(GatewayOutcome.Approved, "simr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant())));

    public Task<GatewayResult> LookupAsync(string key, CancellationToken ct) =>
        Task.FromResult(_settled.TryGetValue(key, out var settled) ? settled
            : _byKey.TryGetValue(key, out var first) ? first
            : new GatewayResult(GatewayOutcome.Unknown, null, Message: "No such request"));

    private GatewayResult Remember(string key, GatewayResult now, GatewayResult later)
    {
        _settled[key] = later;
        return now;
    }
}
