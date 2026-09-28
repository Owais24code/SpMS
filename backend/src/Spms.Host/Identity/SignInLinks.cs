using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Spms.Modules.Core.Data;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Identity;

public sealed record SignInInput(string? ObjectId, string? Email);
public sealed record LocalSignInInput(string? Email, string? Password);

/// <summary>
/// Gives a staff member their sign-in: an SpMS principal linked to their Entra
/// account (issuer + object id). Until then a staff record is only a name on a
/// roster; a role cannot be approved for them. One sign-in per person and per
/// Entra account; the link is audited, and it is a security administrator's
/// act, not HR's (can_propose_role).
/// </summary>
public static class SignInLinks
{
    public static IEndpointRouteBuilder MapSignInLinks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/staff/{id:guid}/sign-in", async (HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access,
            IConfiguration config, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_role", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<SignInInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (!Guid.TryParse(i.ObjectId, out var oid)) return WebApi.Invalid(ctx, "objectId is the person's Entra object id (a GUID).");
            if (i.Email is { Length: > 254 }) return WebApi.Invalid(ctx, "email is at most 254 characters.");

            var issuer = Operations.Provisioning.IssuerFrom(config);
            var subject = oid.ToString();
            var r = await master.ChangeAwaitAsync<StaffRow>(s => s.StaffId == id, version, "workforce.staff.sign_in_link", "staff", s => s.StaffId,
                async s =>
                {
                    if (s.PrincipalId is not null) return "This person already has a sign-in.";
                    if (s.EmploymentStatus == "Terminated") return "A terminated staff member cannot be given a sign-in.";
                    if (await db.Set<PrincipalLoginRow>().AnyAsync(l => l.IdpIssuer == issuer && l.IdpSubject == subject, ct))
                        return "That Entra account is already linked to someone in SpMS.";
                    var principal = new PrincipalRow { PrincipalId = Uuid7.New(), PrincipalType = "Staff", DisplayName = s.PreferredName };
                    db.Add(principal);
                    db.Add(new PrincipalLoginRow
                    {
                        PrincipalLoginId = Uuid7.New(), PrincipalId = principal.PrincipalId, LoginType = "EntraUser",
                        IdpIssuer = issuer, IdpSubject = subject, Username = string.IsNullOrWhiteSpace(i.Email) ? null : i.Email.Trim(),
                    });
                    s.PrincipalId = principal.PrincipalId;
                    return null;
                }, ct, after: s => new { s.StaffId, s.PrincipalId, issuer, objectId = subject, email = i.Email });

            return r.ToHttp(http, ctx, s => new
            {
                staffId = s.StaffId, s.PreferredName, s.PrincipalId, hasSignIn = s.PrincipalId != null,
                rowVersion = s.Version, eTag = $"\"{s.Version}\"",
            });
        });
        /* local email + password accounts (Auth:Local:Enabled) */

        // Who has which kind of sign-in: the staff screen's sign-in panel, sign-ups waiting for approval.
        app.MapGet("/staff/sign-ins", async (HttpContext http, SpmsDbContext db, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Gate(ctx, access, ct) is { } refused) return refused;
            var now = DateTimeOffset.UtcNow;
            var rows = await (from s in db.Set<StaffRow>().AsNoTracking()
                              join p in db.Set<PrincipalRow>().AsNoTracking() on s.PrincipalId equals p.PrincipalId
                              join l in db.Set<PrincipalLoginRow>().AsNoTracking() on p.PrincipalId equals l.PrincipalId
                              where l.LoginType != "EntraApplication"
                              select new { s.StaffId, s.PreferredName, s.EmploymentStatus, p.PrincipalId, principalStatus = p.Status,
                                           l.LoginType, l.Username, l.MustChangePassword, l.LockedUntil, l.LastAuthenticatedAt })
                .ToListAsync(ct);
            return Results.Json(rows.Select(r => new
            {
                r.StaffId, r.PreferredName, r.PrincipalId, loginType = r.LoginType, email = r.Username,
                pendingApproval = r.principalStatus == "Disabled" && r.EmploymentStatus == "Pending",
                active = r.principalStatus == "Active", r.MustChangePassword, locked = r.LockedUntil > now,
                lastSignInUtc = r.LastAuthenticatedAt?.ToUniversalTime().ToString("O"),
            }), Json.Options);
        });

        // An administrator gives an existing staff member an email + password sign-in, with a temporary password.
        app.MapPost("/staff/{id:guid}/local-sign-in", async (HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access,
            Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Gate(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<LocalSignInInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (!Passwords.LooksLikeEmail(i.Email)) return WebApi.Invalid(ctx, "email is the address they will sign in with.");
            var temporary = string.IsNullOrEmpty(i.Password) ? Passwords.Temporary() : i.Password;
            if (Passwords.Problem(temporary, i.Email) is { } problem) return WebApi.Invalid(ctx, problem);
            var subject = Passwords.Subject(i.Email!);

            var r = await master.ChangeAwaitAsync<StaffRow>(s => s.StaffId == id, version, "workforce.staff.local_sign_in", "staff", s => s.StaffId,
                async s =>
                {
                    if (s.PrincipalId is not null) return "This person already has a sign-in.";
                    if (s.EmploymentStatus == "Terminated") return "A terminated staff member cannot be given a sign-in.";
                    if (await db.Set<PrincipalLoginRow>().AnyAsync(l => l.IdpIssuer == LocalSessionIssuer.Issuer && l.IdpSubject == subject, ct))
                        return "That email already signs in to SpMS.";
                    var principal = new PrincipalRow { PrincipalId = Uuid7.New(), PrincipalType = "Staff", DisplayName = s.PreferredName };
                    db.Add(principal);
                    db.Add(new PrincipalLoginRow
                    {
                        PrincipalLoginId = Uuid7.New(), PrincipalId = principal.PrincipalId, LoginType = "Local", IdpIssuer = LocalSessionIssuer.Issuer,
                        IdpSubject = subject, Username = i.Email!.Trim(), MfaRequired = false, PasswordHash = Passwords.Hash(temporary),
                        MustChangePassword = true, CredentialRotatedAt = DateTimeOffset.UtcNow,
                    });
                    s.PrincipalId = principal.PrincipalId;
                    return null;
                }, ct, after: s => new { s.StaffId, s.PrincipalId, loginType = "Local", email = i.Email });

            return r.ToHttp(http, ctx, s => new
            {
                staffId = s.StaffId, s.PreferredName, s.PrincipalId, hasSignIn = true, email = i.Email!.Trim(), temporaryPassword = temporary,
                rowVersion = s.Version, eTag = $"\"{s.Version}\"",
            });
        });

        // A forgotten password: a new temporary one, the lock lifted, every earlier session ended.
        app.MapPost("/staff/{id:guid}/reset-password", async (HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access,
            IMemoryCache cache, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Gate(ctx, access, ct) is { } refused) return refused;
            var principal = await db.Set<StaffRow>().AsNoTracking().Where(s => s.StaffId == id).Select(s => s.PrincipalId).SingleOrDefaultAsync(ct);
            var login = principal is null ? null
                : await db.Set<PrincipalLoginRow>().SingleOrDefaultAsync(l => l.PrincipalId == principal && l.LoginType == "Local", ct);
            if (login is null) return WebApi.NotFound(ctx);
            var temporary = Passwords.Temporary();
            login.PasswordHash = Passwords.Hash(temporary);
            login.MustChangePassword = true;
            login.FailedAttempts = 0;
            login.LockedUntil = null;
            login.CredentialRotatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await master.AuditAsync(new AuditEntry("core.principal_login.reset_password", "principal_login", login.PrincipalLoginId.ToString(), login.Version,
                AfterData: new { staffId = id, login.Username }), ct);
            await db.SaveChangesAsync(ct);
            cache.Remove(LocalAccounts.RotationKey(login.IdpSubject));
            return Results.Json(new { staffId = id, email = login.Username, temporaryPassword = temporary }, Json.Options);
        });

        // A sign-up from the Register page becomes a working account (still with no roles until they are approved too), or is turned down.
        app.MapPost("/staff/{id:guid}/approve-sign-up", (HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access, Guid id, CancellationToken ct) =>
            DecideSignUpAsync(http, db, master, access, id, approve: true, ct));
        app.MapPost("/staff/{id:guid}/reject-sign-up", (HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access, Guid id, CancellationToken ct) =>
            DecideSignUpAsync(http, db, master, access, id, approve: false, ct));

        return app;
    }

    private static Task<IResult?> Gate(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_role", Fga.Tenant(ctx.TenantId), ct);

    private static async Task<IResult> DecideSignUpAsync(HttpContext http, SpmsDbContext db, MasterData master, IAccessDecider access, Guid id,
        bool approve, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (await Gate(ctx, access, ct) is { } refused) return refused;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        var r = await master.ChangeAwaitAsync<StaffRow>(s => s.StaffId == id, version,
            approve ? "workforce.staff.approve_sign_up" : "workforce.staff.reject_sign_up", "staff", s => s.StaffId,
            async s =>
            {
                if (s.EmploymentStatus != "Pending" || s.PrincipalId is null) return "This is not a sign-up waiting for approval.";
                var principal = await db.Set<PrincipalRow>().SingleAsync(p => p.PrincipalId == s.PrincipalId, ct);
                if (principal.Status != "Disabled") return "This is not a sign-up waiting for approval.";
                if (approve)
                {
                    principal.Status = "Active";
                    s.EmploymentStatus = "Active";
                }
                else
                {
                    s.EmploymentStatus = "Terminated";
                    foreach (var l in await db.Set<PrincipalLoginRow>().Where(l => l.PrincipalId == s.PrincipalId).ToListAsync(ct)) l.Status = "Disabled";
                }
                return null;
            }, ct, after: s => new { s.StaffId, s.PrincipalId, s.EmploymentStatus });
        return r.ToHttp(http, ctx, s => new
        {
            staffId = s.StaffId, s.PreferredName, s.PrincipalId, s.EmploymentStatus, hasSignIn = s.PrincipalId != null,
            rowVersion = s.Version, eTag = $"\"{s.Version}\"",
        });
    }
}
