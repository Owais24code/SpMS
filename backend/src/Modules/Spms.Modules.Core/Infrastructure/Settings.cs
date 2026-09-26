using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Core.Infrastructure;

/// <summary>
/// Reads governed settings (core.setting): the one Active value for a key at
/// this instant, the property's own over the tenant's. Callers pass a default
/// for when nothing is configured, so an unconfigured property behaves the
/// same everywhere rather than each module inventing its own fallback.
/// </summary>
public sealed class SettingsReader(SpmsDbContext db, IClock clock)
{
    public async Task<JsonElement?> GetAsync(string key, CancellationToken ct, string deploymentScope = "Production")
    {
        var now = clock.UtcNow;
        var property = db.Scope.CurrentPropertyId;
        var rows = await db.Set<SettingRow>().AsNoTracking()
            .Where(s => s.SettingKey == key && s.DeploymentScope == deploymentScope && (s.Status == SettingStatuses.Active || s.Status == SettingStatuses.Approved)
                        && s.EffectiveFrom <= now && (s.EffectiveTo == null || s.EffectiveTo > now)
                        && (s.PropertyId == null || s.PropertyId == property))
            .OrderByDescending(s => s.PropertyId != null)
            .Select(s => s.ValueJson)
            .Take(1).ToListAsync(ct);
        return rows.Count == 0 ? null : JsonDocument.Parse(rows[0]).RootElement.Clone();
    }

    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken ct)
    {
        var v = await GetAsync(key, ct);
        if (v is null) return fallback;
        try { return v.Value.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? fallback; }
        catch (JsonException) { return fallback; }
    }
}
