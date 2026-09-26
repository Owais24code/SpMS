using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Core.Infrastructure;

/// <summary>
/// Legal holds (SEC-005): a retention or erasure job asks before it removes
/// anything. A hold names a table and a key; an Active hold that has started
/// and not ended protects that row whatever its retention says.
/// </summary>
public sealed class LegalHolds(SpmsDbContext db, IClock clock)
{
    public async Task<IReadOnlySet<string>> HeldKeysAsync(string entityTable, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return (await db.Set<LegalHoldRow>().AsNoTracking()
            .Where(h => h.EntityTable == entityTable && h.Status == "Active" && h.StartsAt <= now && (h.EndsAt == null || h.EndsAt > now))
            .Select(h => h.EntityKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>Idempotency records are kept until they expire and then deleted: a replay after that is a new request.</summary>
public sealed class IdempotencyRetentionJob(SpmsDbContext db, IClock clock) : IPropertyJob
{
    public string Name => "core.retention.idempotency";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var ids = await db.Set<IdempotencyRecordRow>().Where(r => r.ExpiresAt < now).OrderBy(r => r.ExpiresAt).Take(1000)
            .Select(r => r.IdempotencyRecordId).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        return await db.Set<IdempotencyRecordRow>().Where(r => ids.Contains(r.IdempotencyRecordId)).ExecuteDeleteAsync(ct);
    }
}
