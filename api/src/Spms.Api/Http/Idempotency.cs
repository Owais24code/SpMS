using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spms.Domain.Concurrency;

namespace Spms.Api.Http;

/// <summary>API-001. Replay returns the original result; a changed body is rejected.</summary>
public static class Idempotency
{
    public const string Header = "Idempotency-Key";

    public static string HashBody<T>(T body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body))));

    public sealed record Check(bool IsReplay, IdempotencyRecord? Record, bool Mismatch);

    public static Check Inspect<T>(
        IIdempotencyStore store, string tenantId, string operation, string? key, T body)
    {
        if (string.IsNullOrWhiteSpace(key)) return new Check(false, null, false);

        if (!store.TryGet(tenantId, operation, key, out var found))
            return new Check(false, null, false);

        return HashBody(body) == found.RequestHash
            ? new Check(true, found, false)
            : new Check(false, found, true);
    }

    public static void Remember<T>(
        IIdempotencyStore store, string tenantId, string operation,
        string? key, T body, int status, object response)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        store.Put(new IdempotencyRecord(
            Key: key,
            Operation: operation,
            TenantId: tenantId,
            RequestHash: HashBody(body),
            StatusCode: status,
            ResponseJson: JsonSerializer.Serialize(response),
            StoredAtUtc: DateTimeOffset.UtcNow));
    }
}
