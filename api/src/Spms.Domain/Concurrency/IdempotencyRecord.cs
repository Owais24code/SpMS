namespace Spms.Domain.Concurrency;

/// <summary>
/// A completed request, keyed by tenant + operation + client key (API-001).
///
/// Replaying the same key with the same body returns the ORIGINAL result —
/// it does not re-execute. A different body under the same key is a client
/// bug and is rejected rather than silently overwriting.
/// </summary>
public sealed record IdempotencyRecord(
    string Key,
    string Operation,
    string TenantId,
    string RequestHash,
    int StatusCode,
    string ResponseJson,
    DateTimeOffset StoredAtUtc);

public interface IIdempotencyStore
{
    bool TryGet(string tenantId, string operation, string key, out IdempotencyRecord record);
    void Put(IdempotencyRecord record);
}
