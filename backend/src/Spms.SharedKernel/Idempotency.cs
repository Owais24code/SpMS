namespace Spms.SharedKernel;

public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims the key. Returns Reserved on a first arrival,
    /// Replay with the stored response on a repeat of the same body,
    /// Mismatch when the body differs, InFlight when another request holds it.
    ///
    /// A read-then-write check let two concurrent requests with one key both
    /// proceed, which is the exact case the key exists to prevent.
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(IdempotencyScope scope, string requestHash, DateTimeOffset nowUtc, CancellationToken ct = default);

    Task CompleteAsync(IdempotencyScope scope, int statusCode, string responseJson, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>Releases a reservation whose request failed, so a retry is possible.</summary>
    Task AbandonAsync(IdempotencyScope scope, CancellationToken ct = default);

    /// <summary>Drops records past their retention. Returns how many.</summary>
    Task<int> SweepAsync(IdempotencyScope scopeForTenant, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// What an idempotency key is scoped to.
///
/// PropertyId is part of it: scoped to tenant and operation alone, the same
/// client-chosen key arriving at two properties replayed the first property's
/// response to the second. Route is part of it because the route id is
/// load-bearing for a reassign.
/// </summary>
public sealed record IdempotencyScope(
    Guid TenantId, Guid PropertyId, Guid? PrincipalId, string Operation, string Route, string Key);

public enum IdempotencyOutcome { Reserved, Replay, Mismatch, InFlight }

public sealed record IdempotencyClaim(IdempotencyOutcome Outcome, int StatusCode = 0, string? ResponseJson = null);
