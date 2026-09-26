using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Spms.Host.Operations;

/// <summary>
/// What a new deployment needs before anyone can sign in: a tenant, its
/// properties, and the first administrators linked to their Entra accounts.
/// Everything else (rooms, services, staff, roles) is then done in the app.
///
///   Provision:Tenant:Code / Name / DataRegion / Currency
///   Provision:Properties:0:Code / Name / Timezone / Currency
///   Provision:Admins:0:ObjectId / Name / Email / Roles:0..
///   Provision:Issuer   (default: Auth:ValidIssuers:0, else Auth:Authority)
/// </summary>
public sealed class ProvisionOptions
{
    public TenantSpec Tenant { get; set; } = new();
    public List<PropertySpec> Properties { get; set; } = [];
    public List<AdminSpec> Admins { get; set; } = [];
    public string? Issuer { get; set; }

    public sealed class TenantSpec
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string DataRegion { get; set; } = "centralindia";
        public string Currency { get; set; } = "USD";
    }

    public sealed class PropertySpec
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Timezone { get; set; }
        public string? Currency { get; set; }
    }

    public sealed class AdminSpec
    {
        public string? ObjectId { get; set; }
        public string? Name { get; set; }
        public string? Email { get; set; }
        /// <summary>Tenant-wide roles. Default: platform_admin, spa_manager and configuration_approver (two admins then approve each other's changes).</summary>
        public List<string> Roles { get; set; } = [];
    }

    public bool Requested => !string.IsNullOrWhiteSpace(Tenant.Code);
}

/// <summary>
/// Idempotent: every id is derived from its natural key (tenant code, property
/// code, the admin's issuer and object id), so a second run inserts nothing and
/// the migrate job can carry the section on every release. It never changes
/// or removes what exists; that is the app's job, with its audit trail.
///
/// The administrators' roles are approved by a "provisioning" service principal
/// (an Active role needs an approver who is not its proposer, SEC-014). The
/// pipeline's `--fga-sync` then writes their tuples.
/// </summary>
public static class Provisioning
{
    public static readonly List<string> DefaultAdminRoles = ["platform_admin", "spa_manager", "configuration_approver"];

    public static string IssuerFrom(IConfiguration config) =>
        config["Provision:Issuer"] is { Length: > 0 } explicitIssuer ? explicitIssuer
        : config.GetSection("Auth:ValidIssuers").Get<string[]>() is [{ Length: > 0 } first, ..] ? first
        : config["Auth:Authority"] is { Length: > 0 } authority ? authority.TrimEnd('/')
        : SpmsAuth.DevIssuer;

    public static async Task<IReadOnlyList<string>> RunAsync(string ownerConnection, string role, ProvisionOptions o, string issuer,
        ILogger logger, CancellationToken ct = default)
    {
        var problems = Validate(o);
        if (problems.Count > 0) throw new InvalidOperationException("Provision: " + string.Join(" ", problems));

        var done = new List<string>();
        var tenant = Id("tenant", o.Tenant.Code!);
        var properties = o.Properties.Select(p => (Spec: p, Id: Id("property", $"{o.Tenant.Code}/{p.Code}"))).ToList();
        var provisioner = Id("principal", $"{o.Tenant.Code}/provisioning");

        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"SET LOCAL ROLE \"{role.Replace("\"", "\"\"")}\"", ct);
        await Exec(conn, tx, "SELECT core.begin_scope(@t, @p, NULL, 'provision')", ct, ("t", tenant), ("p", properties.Select(p => p.Id).ToArray()));

        if (await Exec(conn, tx, """
                INSERT INTO core.tenant (tenant_id, code, name, data_region, default_currency_code)
                VALUES (@id, @code, @name, @region, @currency) ON CONFLICT DO NOTHING
                """, ct, ("id", tenant), ("code", o.Tenant.Code!), ("name", o.Tenant.Name ?? o.Tenant.Code!),
                ("region", o.Tenant.DataRegion), ("currency", o.Tenant.Currency.ToUpperInvariant())) == 1)
            done.Add($"tenant {o.Tenant.Code}");

        foreach (var (p, id) in properties)
            if (await Exec(conn, tx, """
                    INSERT INTO core.property (property_id, tenant_id, code, name, timezone, currency_code)
                    VALUES (@id, @t, @code, @name, @tz, @currency) ON CONFLICT DO NOTHING
                    """, ct, ("id", id), ("t", tenant), ("code", p.Code!), ("name", p.Name ?? p.Code!), ("tz", p.Timezone!),
                    ("currency", (p.Currency ?? o.Tenant.Currency).ToUpperInvariant())) == 1)
                done.Add($"property {p.Code}");

        await Exec(conn, tx, """
            INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name)
            VALUES (@id, @t, 'Service', 'SpMS provisioning') ON CONFLICT DO NOTHING
            """, ct, ("id", provisioner), ("t", tenant));

        foreach (var a in o.Admins)
        {
            var subject = a.ObjectId!.Trim().ToLowerInvariant();
            // Already signed in to SpMS (linked here earlier, or in the app): leave that person exactly as they are.
            await using (var existing = new NpgsqlCommand("SELECT 1 FROM core.principal_login WHERE idp_issuer = @i AND idp_subject = @s", conn, tx))
            {
                existing.Parameters.AddWithValue("i", issuer);
                existing.Parameters.AddWithValue("s", subject);
                if (await existing.ExecuteScalarAsync(ct) is not null) continue;
            }

            var principal = Id("principal", $"{issuer}/{subject}");
            var staff = Id("staff", $"{issuer}/{subject}");
            await Exec(conn, tx, """
                INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name)
                VALUES (@id, @t, 'Staff', @name) ON CONFLICT DO NOTHING
                """, ct, ("id", principal), ("t", tenant), ("name", a.Name!));
            await Exec(conn, tx, """
                INSERT INTO core.principal_login (principal_login_id, tenant_id, principal_id, login_type, idp_issuer, idp_subject, username)
                VALUES (@id, @t, @p, 'EntraUser', @i, @s, @u)
                """, ct, ("id", Id("login", $"{issuer}/{subject}")), ("t", tenant), ("p", principal), ("i", issuer), ("s", subject),
                ("u", (object?)a.Email ?? DBNull.Value));
            await Exec(conn, tx, """
                INSERT INTO workforce.staff (staff_id, tenant_id, principal_id, preferred_name, bookable)
                VALUES (@id, @t, @p, @name, false) ON CONFLICT DO NOTHING
                """, ct, ("id", staff), ("t", tenant), ("p", principal), ("name", a.Name!));
            var roles = a.Roles.Count > 0 ? a.Roles : DefaultAdminRoles;
            foreach (var r in roles)
                await Exec(conn, tx, """
                    INSERT INTO workforce.staff_role_assignment
                           (staff_role_assignment_id, tenant_id, property_id, staff_id, role_code, status, approved_by, approved_at)
                    VALUES (@id, @t, NULL, @s, @r, 'Active', @by, now()) ON CONFLICT DO NOTHING
                    """, ct, ("id", Id("role", $"{staff}/{r}")), ("t", tenant), ("s", staff), ("r", r), ("by", provisioner));
            done.Add($"admin {a.Name} ({string.Join(", ", roles)})");
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Provisioning for tenant {Tenant} ({TenantId}): {Done}", o.Tenant.Code, tenant,
            done.Count == 0 ? "nothing new" : string.Join("; ", done));
        return done;
    }

    private static List<string> Validate(ProvisionOptions o)
    {
        var problems = new List<string>();
        if (!IsCode(o.Tenant.Code)) problems.Add("Tenant:Code is 2–40 lower-case letters, digits or dashes.");
        if (!IsCurrency(o.Tenant.Currency)) problems.Add("Tenant:Currency is an ISO 4217 code.");
        if (o.Properties.Count == 0) problems.Add("At least one property is required.");
        foreach (var p in o.Properties)
        {
            if (!IsCode(p.Code)) problems.Add($"Property code '{p.Code}' is 2–40 lower-case letters, digits or dashes.");
            if (string.IsNullOrWhiteSpace(p.Timezone) || !TimeZoneInfo.TryFindSystemTimeZoneById(p.Timezone, out _))
                problems.Add($"Property '{p.Code}' needs an IANA timezone (e.g. Asia/Kolkata).");
            if (p.Currency is { } c && !IsCurrency(c)) problems.Add($"Property '{p.Code}' currency is an ISO 4217 code.");
        }
        if (o.Admins.Count == 0) problems.Add("At least one admin is required.");
        foreach (var a in o.Admins)
        {
            if (!Guid.TryParse(a.ObjectId, out _)) problems.Add($"Admin '{a.Name}' needs an Entra object id (a GUID).");
            if (string.IsNullOrWhiteSpace(a.Name)) problems.Add("Every admin needs a name.");
        }
        return problems;

        static bool IsCode(string? s) => s is { Length: >= 2 and <= 40 } && s.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
        static bool IsCurrency(string? s) => s is { Length: 3 } && s.All(char.IsAsciiLetter);
    }

    /// <summary>A stable id for a natural key: SHA-256, shaped as a version-8 (custom) UUID.</summary>
    public static Guid Id(string kind, string key)
    {
        var b = SHA256.HashData(Encoding.UTF8.GetBytes($"spms:{kind}:{key}"))[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x80);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }

    private static async Task<int> Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
