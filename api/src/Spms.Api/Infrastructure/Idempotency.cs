using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Spms.Domain.Abstractions;
using Spms.Domain.Errors;

namespace Spms.Api.Infrastructure;

/// <summary>
/// API-001. A create or commit carrying an Idempotency-Key replays its
/// original result on a repeat, and is rejected if the same key arrives with
/// a different body.
///
/// Claim-then-complete, not check-then-write: the reservation is taken
/// atomically before the work starts. The previous read-then-write version let
/// two concurrent requests holding one key both proceed, which is the single
/// case the key exists to prevent.
/// </summary>
public static class Idempotency
{
    /// <summary>
    /// What an endpoint returns from the protected body: the status and the
    /// serialized response, plus whether that result should be remembered for
    /// a replay. A refusal is not remembered, so the corrected retry may reuse
    /// the key.
    /// </summary>
    public sealed record Outcome(int StatusCode, string ResponseJson, bool Durable, IResult Result);

    /// <summary>
    /// Runs <paramref name="work"/> under an idempotency claim, releasing the
    /// reservation on every path that did not produce a durable result.
    ///
    /// This wrapper exists because the claim / try / finally / completed-flag
    /// scaffold was copy-pasted into each mutating endpoint, and an endpoint
    /// that forgets the release leaves its key answering 409 until the lease
    /// expires.
    /// </summary>
    public static async Task<IResult> RunAsync(
        HttpContext http,
        IIdempotencyStore store,
        RequestContext ctx,
        ILogger logger,
        string operation,
        string body,
        DateTimeOffset nowUtc,
        Func<Task<Outcome>> work,
        CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

        // No key: the caller has opted out of replay protection.
        if (string.IsNullOrWhiteSpace(key)) return (await work()).Result;

        if (key.Length > 200)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "Idempotency-Key may not exceed 200 characters.");

        var scope = new IdempotencyScope(
            ctx.TenantId, ctx.PropertyId, operation, http.Request.Path.Value ?? "/", key);

        // The route is in the hash as well as the scope: the route id is
        // load-bearing for a reassign, so two reassigns of different
        // appointments with one key and identical bodies replayed each other.
        var hash = Hash($"{http.Request.Method}\n{http.Request.Path}\n{body}");

        var claim = await store.ClaimAsync(scope, hash, nowUtc, ct);

        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Replay:
                return Results.Content(claim.ResponseJson ?? "{}", "application/json", statusCode: claim.StatusCode);

            case IdempotencyOutcome.Mismatch:
                return Problem.From(ApiError.IdempotencyMismatch, ctx.CorrelationId,
                    "This Idempotency-Key was already used with a different request. Use a new key, or resend the original request unchanged.");

            case IdempotencyOutcome.InFlight:
                // 409 rather than a wait: the client already has a retry path,
                // and blocking a worker on a peer request is how a thread pool
                // starves.
                return Problem.From(ApiError.IdempotencyMismatch, ctx.CorrelationId,
                    "A request with this Idempotency-Key is still in flight. Retry shortly.",
                    retryable: true, retryAfterSeconds: 2);
        }

        var durable = false;
        try
        {
            var outcome = await work();
            if (outcome.Durable)
            {
                await store.CompleteAsync(scope, outcome.StatusCode, outcome.ResponseJson, nowUtc, ct);
                durable = true;
            }
            return outcome.Result;
        }
        finally
        {
            if (!durable)
            {
                try
                {
                    // CancellationToken.None deliberately: if the client walked
                    // away the reservation still has to be released, and ct is
                    // already cancelled on that path.
                    await store.AbandonAsync(scope, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // An await in a finally that throws would replace the return
                    // value, or swallow an in-flight exception. The in-memory
                    // store cannot throw; Redis or SQL can.
                    logger.LogError(ex, "Failed to release idempotency key for {Operation} {CorrelationId}",
                        operation, ctx.CorrelationId);
                }
            }
        }
    }

    /// <summary>A result that must not be remembered for replay.</summary>
    public static Outcome Refused(IResult result) => new(0, string.Empty, Durable: false, result);

    private static string Hash(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
