using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Identity;

public sealed record LoginInput(string? Email, string? Password);
public sealed record RegisterInput(string? Email, string? Name, string? Password);
public sealed record ChangePasswordInput(string? CurrentPassword, string? NewPassword);

/// <summary>
/// Local email + password accounts. A local login is a core.principal_login
/// row (login_type Local, issuer spms-local, subject = lower-cased email), so
/// once the password is checked the caller is resolved exactly like an Entra
/// user: tenant, roles and properties come from SpMS.
///
///   sign-in        wrong passwords count; after Auth:Local:MaxFailedAttempts
///                  the login is locked for LockoutMinutes. An unknown email
///                  costs the same time as a wrong password, and both get the
///                  same answer.
///   registration   creates a disabled principal and a Pending staff record
///                  with no roles; nothing works until an administrator approves.
///   passwords      a temporary one (from an administrator) must be changed at
///                  first sign-in; changing or resetting a password ends every
///                  session issued before it.
/// </summary>
public sealed class LocalAccounts(NpgsqlDataSource dataSource, PersistenceOptions persistence, LocalAuthOptions options,
    LocalSessionIssuer issuer, IMemoryCache cache, IConfiguration config, IClock clock, ILogger<LocalAccounts> logger)
{
    public enum Outcome { Ok, Failed, Locked, PendingApproval, NoRoles }

    public sealed record LoginResult(Outcome Outcome, LocalSession? Session = null, DateTimeOffset? LockedUntil = null);

    private sealed record Login(Guid LoginId, Guid Principal, Guid Tenant, string PrincipalStatus, string? Hash, bool MustChange,
        int Failed, DateTimeOffset? LockedUntil, string DisplayName, string? StaffStatus, bool HasRoles);

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        var subject = Passwords.Subject(email);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var login = await FindAsync(conn, tx, subject, lockRow: true, ct);
        if (login is null)
        {
            Passwords.Waste(password);
            return new(Outcome.Failed);
        }

        var now = clock.UtcNow;
        if (login.LockedUntil is { } until && until > now) return new(Outcome.Locked, LockedUntil: until);

        if (!Passwords.Verify(password, login.Hash))
        {
            var failed = login.Failed + 1;
            var lockNow = failed >= options.MaxFailedAttempts;
            await ExecAsync(conn, tx, """
                UPDATE core.principal_login
                   SET failed_attempts = @f, locked_until = @l, version = version + 1
                 WHERE principal_login_id = @id
                """, ct, ("f", lockNow ? 0 : failed), ("l", lockNow ? now.AddMinutes(options.LockoutMinutes) : (object)DBNull.Value), ("id", login.LoginId));
            await tx.CommitAsync(ct);
            if (lockNow) logger.LogWarning("Local login {Login} locked after {Attempts} wrong passwords", login.LoginId, options.MaxFailedAttempts);
            return lockNow ? new(Outcome.Locked, LockedUntil: now.AddMinutes(options.LockoutMinutes)) : new(Outcome.Failed);
        }

        // The password is right: only now say whether the account is waiting for approval.
        if (login.PrincipalStatus != "Active")
            return login.StaffStatus == "Pending" ? new(Outcome.PendingApproval) : new(Outcome.Failed);

        await ExecAsync(conn, tx, """
            UPDATE core.principal_login
               SET failed_attempts = 0, locked_until = NULL, last_authenticated_at = now(), version = version + 1
             WHERE principal_login_id = @id
            """, ct, ("id", login.LoginId));
        await tx.CommitAsync(ct);
        // A temporary password can always be replaced; otherwise a session is only worth issuing to someone with a role.
        if (!login.MustChange && !login.HasRoles) return new(Outcome.NoRoles);
        return new(Outcome.Ok, issuer.Issue(subject, login.DisplayName, login.MustChange));
    }

    /// <summary>202 whatever happens (an existing email is not revealed); false only when registration is off or not configured.</summary>
    public async Task<bool> RegisterAsync(string email, string name, string password, CancellationToken ct)
    {
        var tenantCode = string.IsNullOrWhiteSpace(options.RegistrationTenant) ? config["Provision:Tenant:Code"] : options.RegistrationTenant;
        var propertyCode = string.IsNullOrWhiteSpace(options.RegistrationProperty) ? config["Provision:Properties:0:Code"] : options.RegistrationProperty;
        if (!options.AllowRegistration || string.IsNullOrWhiteSpace(tenantCode) || string.IsNullOrWhiteSpace(propertyCode)) return false;

        var subject = Passwords.Subject(email);
        var hash = Passwords.Hash(password);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ScopeSql.ApplyAsync(conn, tx, new ExecutionScope(), persistence.RuntimeRole, ct);

        Guid tenant, property;
        await using (var cmd = new NpgsqlCommand("SELECT tenant_id, property_id FROM core.resolve_property(@t, @p)", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", tenantCode);
            cmd.Parameters.AddWithValue("p", propertyCode);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            tenant = r.GetGuid(0);
            property = r.GetGuid(1);
        }
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM core.resolve_principal(@i, @s)", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", LocalSessionIssuer.Issuer);
            cmd.Parameters.AddWithValue("s", subject);
            if (await cmd.ExecuteScalarAsync(ct) is not null) return true; // already registered: same answer, nothing written
        }

        var scope = new ExecutionScope();
        scope.Set(tenant, [property], property, null, ActorType.System, "registration");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);
        var principal = Uuid7.New();
        await ExecAsync(conn, tx, """
            INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name, status)
            VALUES (@p, @t, 'Staff', @n, 'Disabled')
            """, ct, ("p", principal), ("t", tenant), ("n", name));
        await ExecAsync(conn, tx, """
            INSERT INTO core.principal_login (principal_login_id, tenant_id, principal_id, login_type, idp_issuer, idp_subject, username,
                                              mfa_required, password_hash, credential_rotated_at)
            VALUES (@id, @t, @p, 'Local', @i, @s, @u, false, @h, now())
            """, ct, ("id", Uuid7.New()), ("t", tenant), ("p", principal), ("i", LocalSessionIssuer.Issuer), ("s", subject),
            ("u", email.Trim()), ("h", hash));
        await ExecAsync(conn, tx, """
            INSERT INTO workforce.staff (staff_id, tenant_id, principal_id, home_property_id, preferred_name, employment_status, bookable)
            VALUES (@id, @t, @p, @h, @n, 'Pending', false)
            """, ct, ("id", Uuid7.New()), ("t", tenant), ("p", principal), ("h", property), ("n", name));
        await tx.CommitAsync(ct);
        logger.LogInformation("Local sign-up {Principal} is waiting for approval", principal);
        return true;
    }

    public async Task<(bool Ok, string? Problem, LocalSession? Session, bool HasRoles)> ChangePasswordAsync(string subject, string current, string next, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var login = await FindAsync(conn, tx, subject, lockRow: true, ct);
        if (login is null || login.PrincipalStatus != "Active") return (false, "Sign in again.", null, false);
        if (!Passwords.Verify(current, login.Hash)) return (false, "The current password is not right.", null, false);
        if (Passwords.Problem(next, subject) is { } problem) return (false, problem, null, false);
        if (Passwords.Verify(next, login.Hash)) return (false, "Choose a password you have not just used.", null, false);

        // The rotation time comes from this process's clock, the same clock that stamps the new token, so the
        // new session is never older than the change that ends the old ones.
        var rotated = clock.UtcNow;
        await ExecAsync(conn, tx, """
            UPDATE core.principal_login
               SET password_hash = @h, must_change_password = false, credential_rotated_at = @r,
                   failed_attempts = 0, locked_until = NULL, version = version + 1
             WHERE principal_login_id = @id
            """, ct, ("h", Passwords.Hash(next)), ("r", rotated), ("id", login.LoginId));
        await tx.CommitAsync(ct);
        cache.Remove(RotationKey(subject));
        return (true, null, issuer.Issue(subject, login.DisplayName, mustChangePassword: false), login.HasRoles);
    }

    /// <summary>When the login's password last changed: a token issued before it is no longer honoured. Cached briefly.</summary>
    public async Task<DateTimeOffset?> CredentialRotatedAtAsync(Guid tenant, string subject, CancellationToken ct)
    {
        if (cache.TryGetValue(RotationKey(subject), out DateTimeOffset? hit)) return hit;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ExecutionScope();
        scope.Set(tenant, [], null, null, ActorType.System, "identity");
        await ScopeSql.ApplyAsync(conn, tx, scope, persistence.RuntimeRole, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT credential_rotated_at FROM core.principal_login WHERE idp_issuer = @i AND idp_subject = @s", conn, tx);
        cmd.Parameters.AddWithValue("i", LocalSessionIssuer.Issuer);
        cmd.Parameters.AddWithValue("s", subject);
        var value = await cmd.ExecuteScalarAsync(ct) switch
        {
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
            DateTimeOffset d => d,
            _ => (DateTimeOffset?)null,
        };
        cache.Set(RotationKey(subject), value, TimeSpan.FromSeconds(30));
        return value;
    }

    public static string RotationKey(string subject) => $"spms.local.rotated:{subject}";

    private async Task<Login?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string subject, bool lockRow, CancellationToken ct)
    {
        await ScopeSql.ApplyAsync(conn, tx, new ExecutionScope(), persistence.RuntimeRole, ct);
        Guid tenant, principal;
        string status;
        await using (var cmd = new NpgsqlCommand("SELECT principal_id, tenant_id, status FROM core.resolve_principal(@i, @s)", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", LocalSessionIssuer.Issuer);
            cmd.Parameters.AddWithValue("s", subject);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            principal = r.GetGuid(0);
            tenant = r.GetGuid(1);
            status = r.GetString(2);
        }
        var scope = new ExecutionScope();
        scope.Set(tenant, [], null, principal, ActorType.System, "identity");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);
        // Every property in scope, so property-level role assignments are visible to the roles check.
        var properties = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT property_id FROM core.property WHERE status = 'Active'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) properties.Add(r.GetGuid(0));
        scope.Set(tenant, properties, null, principal, ActorType.System, "identity");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);
        await using (var cmd = new NpgsqlCommand($"""
            SELECT l.principal_login_id, l.password_hash, l.must_change_password, l.failed_attempts, l.locked_until, p.display_name,
                   (SELECT s.employment_status FROM workforce.staff s WHERE s.principal_id = l.principal_id LIMIT 1),
                   EXISTS (SELECT 1 FROM workforce.staff_role_assignment a JOIN workforce.staff s ON s.staff_id = a.staff_id
                            WHERE s.principal_id = l.principal_id AND a.status = 'Active'
                              AND a.effective_from <= now() AND (a.effective_to IS NULL OR a.effective_to > now()))
              FROM core.principal_login l
              JOIN core.principal p ON p.principal_id = l.principal_id
             WHERE l.idp_issuer = @i AND l.idp_subject = @s AND l.login_type = 'Local' AND l.status = 'Active'
             {(lockRow ? "FOR UPDATE OF l" : "")}
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("i", LocalSessionIssuer.Issuer);
            cmd.Parameters.AddWithValue("s", subject);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new Login(r.GetGuid(0), principal, tenant, status, r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7));
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>/auth/login, /auth/register, /auth/change-password: outside the request transaction (no tenant yet, or the caller's own row).</summary>
public static class LocalAccountEndpoints
{
    public static IEndpointRouteBuilder MapLocalAccounts(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", async (HttpContext http, LocalAccounts accounts, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            var (i, fail) = await WebApi.BodyAsync<LoginInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.Email) || string.IsNullOrEmpty(i.Password) || i.Password.Length > Passwords.MaxLength)
                return WebApi.Invalid(ctx, "Enter your email and password.");
            var r = await accounts.LoginAsync(i.Email, i.Password, ct);
            return r.Outcome switch
            {
                LocalAccounts.Outcome.Ok => Results.Json(new
                {
                    token = r.Session!.Token, expiresUtc = r.Session.ExpiresAt.ToUniversalTime().ToString("O"),
                    mustChangePassword = r.Session.MustChangePassword,
                }, Json.Options),
                LocalAccounts.Outcome.Locked => Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId,
                    $"Too many wrong passwords. Try again after {r.LockedUntil!.Value.ToUniversalTime():HH:mm} UTC, or ask an administrator to reset it.",
                    extensions: Problem.Ext("reason", "ACCOUNT_LOCKED")),
                LocalAccounts.Outcome.NoRoles => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId,
                    "Your account is active but has no role yet. Ask an administrator to give you one.", extensions: Problem.Ext("reason", "NO_ROLES")),
                LocalAccounts.Outcome.PendingApproval => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId,
                    "Your account is waiting for an administrator to approve it.", extensions: Problem.Ext("reason", "PENDING_APPROVAL")),
                _ => Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId, "The email or password is not right."),
            };
        }).WithMetadata(new NoRequestTransaction());

        app.MapPost("/auth/register", async (HttpContext http, LocalAccounts accounts, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            var (i, fail) = await WebApi.BodyAsync<RegisterInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (!Passwords.LooksLikeEmail(i.Email)) return WebApi.Invalid(ctx, "Enter a valid email address.");
            if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 80) return WebApi.Invalid(ctx, "Enter your name (up to 80 characters).");
            if (Passwords.Problem(i.Password, i.Email) is { } problem) return WebApi.Invalid(ctx, problem);
            return await accounts.RegisterAsync(i.Email!, i.Name.Trim(), i.Password!, ct)
                ? Results.Json(new { status = "PendingApproval", detail = "Thanks. An administrator will approve your account before you can sign in." },
                    Json.Options, statusCode: 202)
                : WebApi.NotFound(ctx);
        }).WithMetadata(new NoRequestTransaction());

        app.MapPost("/auth/change-password", async (HttpContext http, LocalAccounts accounts, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            // The token's own claims, not the resolved context: someone with no role yet must still be able to replace a temporary password.
            if (http.User.Identity?.IsAuthenticated != true || http.User.FindFirst("iss")?.Value != LocalSessionIssuer.Issuer
                || http.User.FindFirst("sub")?.Value is not { Length: > 0 } subject)
                return Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId, "Sign in with your email and password first.");
            var (i, fail) = await WebApi.BodyAsync<ChangePasswordInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrEmpty(i.CurrentPassword) || string.IsNullOrEmpty(i.NewPassword)) return WebApi.Invalid(ctx, "Enter the current and the new password.");
            var (ok, problem, session, hasRoles) = await accounts.ChangePasswordAsync(subject, i.CurrentPassword, i.NewPassword, ct);
            return ok
                ? Results.Json(new
                {
                    token = session!.Token, expiresUtc = session.ExpiresAt.ToUniversalTime().ToString("O"), mustChangePassword = false,
                    hasRoles,
                }, Json.Options)
                : WebApi.Invalid(ctx, problem!);
        }).WithMetadata(new NoRequestTransaction());

        return app;
    }
}
