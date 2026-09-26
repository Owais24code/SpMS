using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host.Identity;

/// <summary>A staff or service principal as SpMS knows it: tenant, roles, properties.</summary>
public sealed record ResolvedIdentity(
    Guid TenantId,
    Guid PrincipalId,
    string PrincipalType,
    string DisplayName,
    Guid? StaffId,
    Guid? HomePropertyId,
    IReadOnlyList<RoleGrant> Roles,
    IReadOnlyList<PropertyRef> Properties)
{
    public IEnumerable<string> RoleCodes => Roles.Select(r => r.Role).Distinct(StringComparer.Ordinal);

    /// <summary>The property to act at: the requested one if allowed, else home, else the first.</summary>
    public Guid? PickProperty(Guid? requested)
    {
        if (requested is { } r) return Properties.Any(p => p.PropertyId == r) ? r : null;
        if (HomePropertyId is { } h && Properties.Any(p => p.PropertyId == h)) return h;
        return Properties.Count > 0 ? Properties[0].PropertyId : null;
    }
}

public sealed record RoleGrant(string Role, Guid? PropertyId);
public sealed record PropertyRef(Guid PropertyId, string Code, string Name, string Timezone);

/// <summary>
/// Resolves an authenticated identity-provider subject to an SpMS principal,
/// then reads what that principal may act on.
///
/// The token proves who the caller is at the identity provider; SpMS decides
/// the tenant, roles and properties (roles and property lists stay out of the
/// token). Before a tenant is known RLS hides everything, so the first step is
/// the narrow SECURITY DEFINER core.resolve_principal; the rest runs as
/// spms_app inside the tenant's scope.
///
/// Cached for a minute per subject. A role revoked in SpMS stops working for
/// new tokens at once and for a live session within that minute; the OpenFGA
/// check on each sensitive action (HIGHER_CONSISTENCY) is what makes
/// revocation immediate where it matters.
/// </summary>
public sealed class PrincipalResolver(NpgsqlDataSource dataSource, PersistenceOptions options, IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public async Task<ResolvedIdentity?> ResolveAsync(string issuer, string subject, CancellationToken ct = default)
    {
        var key = $"spms.principal:{issuer}\n{subject}";
        if (cache.TryGetValue(key, out ResolvedIdentity? hit)) return hit;

        var resolved = await LoadAsync(issuer, subject, ct);
        cache.Set(key, resolved, CacheFor);
        return resolved;
    }

    private async Task<ResolvedIdentity?> LoadAsync(string issuer, string subject, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var anonymous = new ExecutionScope();
        await ScopeSql.ApplyAsync(conn, tx, anonymous, options.RuntimeRole, ct);

        Guid principal, tenant;
        string type;
        await using (var cmd = new NpgsqlCommand(
            "SELECT principal_id, tenant_id, principal_type, status FROM core.resolve_principal(@i, @s)", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", issuer);
            cmd.Parameters.AddWithValue("s", subject);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            if (r.GetString(3) != "Active") return null;
            principal = r.GetGuid(0);
            tenant = r.GetGuid(1);
            type = r.GetString(2);
        }

        // Phase 1: tenant scope only — enough to list the tenant's properties.
        var scope = new ExecutionScope();
        scope.Set(tenant, [], null, principal, ActorType.System, "identity");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);

        var properties = new List<PropertyRef>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT property_id, code, name, timezone FROM core.property WHERE status = 'Active' ORDER BY name", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) properties.Add(new PropertyRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        // Phase 2: every property, so property-scoped role rows are visible.
        scope.Set(tenant, properties.Select(p => p.PropertyId).ToArray(), null, principal, ActorType.System, "identity");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);

        string displayName;
        await using (var cmd = new NpgsqlCommand("SELECT display_name FROM core.principal WHERE principal_id = @p", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", principal);
            displayName = (string?)await cmd.ExecuteScalarAsync(ct) ?? "unknown";
        }

        Guid? staffId = null, home = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT staff_id, home_property_id FROM workforce.staff WHERE principal_id = @p AND employment_status = 'Active'", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", principal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                staffId = r.GetGuid(0);
                home = r.IsDBNull(1) ? null : r.GetGuid(1);
            }
        }

        var roles = new List<RoleGrant>();
        if (staffId is { } sid)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT role_code, property_id FROM workforce.staff_role_assignment
                 WHERE staff_id = @s AND status = 'Active'
                   AND effective_from <= now() AND (effective_to IS NULL OR effective_to > now())
                """, conn, tx);
            cmd.Parameters.AddWithValue("s", sid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(new RoleGrant(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1)));
        }

        await using (var touch = new NpgsqlCommand("""
            UPDATE core.principal_login SET last_authenticated_at = now(), version = version + 1
             WHERE idp_issuer = @i AND idp_subject = @s
            """, conn, tx))
        {
            touch.Parameters.AddWithValue("i", issuer);
            touch.Parameters.AddWithValue("s", subject);
            await touch.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        // A tenant-wide role reaches every property; otherwise only the properties named.
        var allowed = roles.Any(r => r.PropertyId is null)
            ? properties
            : properties.Where(p => roles.Any(r => r.PropertyId == p.PropertyId)).ToList();

        return new ResolvedIdentity(tenant, principal, type, displayName, staffId, home, roles, allowed);
    }
}
