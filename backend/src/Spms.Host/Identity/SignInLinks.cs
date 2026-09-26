using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Identity;

public sealed record SignInInput(string? ObjectId, string? Email);

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
        return app;
    }
}
