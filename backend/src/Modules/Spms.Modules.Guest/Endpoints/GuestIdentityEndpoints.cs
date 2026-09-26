using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Identity;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Guest.Endpoints;

/// <summary>
/// Guest identity: contact points, magic links and guest sessions.
///
///   POST /guests/{id}/contact-points          staff records a contact method (encrypted, hashed, masked)
///   POST /guests/{id}/magic-links             staff sends the guest a sign-in or manage-booking link
///   POST /guest/magic-links/request           a guest asks for a link by email (always 202: no enumeration)
///   POST /guest/sessions                      a guest redeems a link for a session token
///   GET  /guest/me                            the signed-in guest
/// </summary>
public static class GuestIdentityEndpoints
{
    public sealed record ContactPointRequest(string? ContactType, string? Value, bool? IsPrimary, bool? VerifiedInPerson);
    public sealed record IssueLinkRequest(string? ContactPointId, string? Purpose, string? ScopeEntityType, string? ScopeEntityId, int? Minutes);
    public sealed record RequestLinkRequest(string? Tenant, string? Property, string? Email);
    public sealed record RedeemRequest(string? Token);

    public static IEndpointRouteBuilder MapGuestIdentity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/guests/{guestId:guid}/contact-points", AddContactPoint);
        app.MapPost("/guests/{guestId:guid}/magic-links", IssueLink);
        app.MapPost("/guest/magic-links/request", RequestLink);
        app.MapPost("/guest/sessions", Redeem);
        app.MapGet("/guest/me", Me);
        return app;
    }

    private static async Task<IResult> AddContactPoint(
        HttpContext http, Guid guestId, SpmsDbContext db, IFieldProtector protector, IAccessDecider access, IAuditSink audit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Guard.RequireAccessAsync(ctx, access, "can_write_guest_profile", Fga.Property(ctx.PropertyId), ct: ct) is { } refused) return refused;

        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<ContactPointRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;

        var type = req!.ContactType ?? "";
        var normalised = ContactValues.Normalise(type, req.Value ?? "");
        if (normalised is null)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "contactType and a valid value are required.",
                extensions: Problem.Ext("field_violations", new[] { new { field = "value", rule = "invalid_for_contact_type" } }));

        var guest = await db.Set<GuestRow>().AsNoTracking().AnyAsync(g => g.GuestId == guestId, ct);
        if (!guest) return Problem.From(ApiError.NotFound, ctx.CorrelationId);

        var (cipher, hash, hint) = ContactValues.Protect(protector, type, normalised);
        var duplicate = await db.Set<GuestContactPointRow>().AsNoTracking()
            .AnyAsync(c => c.GuestId == guestId && c.ContactType == type && c.LookupHash == hash && c.Status != GuestContactPointStatuses.Retired, ct);
        if (duplicate)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "That contact method is already on the guest's record.");

        var primary = req.IsPrimary ?? false;
        if (primary)
        {
            // One active primary per type (a partial unique index enforces it).
            await db.Set<GuestContactPointRow>()
                .Where(c => c.GuestId == guestId && c.ContactType == type && c.IsPrimary && c.Status == GuestContactPointStatuses.Active)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsPrimary, false).SetProperty(c => c.Version, c => c.Version + 1), ct);
        }

        var row = new GuestContactPointRow
        {
            GuestId = guestId,
            ContactType = type,
            ContactCipher = cipher.Cipher,
            KeyVersion = cipher.KeyVersion,
            LookupHash = hash,
            DisplayHint = hint,
            IsPrimary = primary,
            // In-person verification is the desk attesting it; otherwise the
            // contact is verified by the guest completing a link sent to it.
            VerifiedAt = req.VerifiedInPerson == true ? DateTimeOffset.UtcNow : null,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("guest.contact_point.add", "guest", guestId.ToString(),
            AfterData: new { contactType = type, hint, verified = row.VerifiedAt is not null }, TenantWide: true), ct);

        return Results.Json(new { contactPointId = row.GuestContactPointId, contactType = type, displayHint = hint,
            isPrimary = primary, verified = row.VerifiedAt is not null }, Json.Options, statusCode: 201);
    }

    private static async Task<IResult> IssueLink(
        HttpContext http, Guid guestId, MagicLinkService links, IAccessDecider access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Guard.RequireAccessAsync(ctx, access, "can_write_guest_profile", Fga.Property(ctx.PropertyId), ct: ct) is { } refused) return refused;

        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<IssueLinkRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (!Guid.TryParse(req!.ContactPointId, out var contactPointId))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "contactPointId is required.");
        Guid? entity = Guid.TryParse(req.ScopeEntityId, out var e) ? e : null;

        var r = await links.IssueAsync(guestId, contactPointId, req.Purpose ?? MagicLinkPurposes.SignIn,
            req.ScopeEntityType, entity, req.Minutes, ct);
        return r.Outcome switch
        {
            MagicLinkService.IssueOutcome.Issued => Results.Json(new
            {
                magicLinkId = r.LinkId, expiresUtc = r.ExpiresUtc, delivery = "queued",
                // Development only (Guest:Links:LogLinks): lets the demo and the sweep follow the link.
                devToken = r.DevToken,
            }, Json.Options, statusCode: 202),
            MagicLinkService.IssueOutcome.UnknownGuest => Problem.From(ApiError.NotFound, ctx.CorrelationId),
            MagicLinkService.IssueOutcome.UnknownContactPoint =>
                Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "No active contact point with that id on this guest."),
            MagicLinkService.IssueOutcome.UnverifiedContactPoint =>
                Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "A link can only be sent to a verified contact method (SEC-011)."),
            _ => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "purpose must be SignIn, ManageBooking, CompleteIntake or Pay."),
        };
    }

    /// <summary>
    /// Anonymous. Always 202 and always the same body, found or not: an answer
    /// that differs would let anyone test which emails are guests here.
    /// </summary>
    private static async Task<IResult> RequestLink(
        HttpContext http, SpmsDbContext db, IFieldProtector protector, MagicLinkService links, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        var accepted = Results.Json(new { accepted = true, detail = "If that address is on file, a sign-in link is on its way." },
            Json.Options, statusCode: 202);

        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RequestLinkRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        var email = ContactValues.Normalise("Email", req!.Email ?? "");
        if (email is null || string.IsNullOrWhiteSpace(req.Tenant) || string.IsNullOrWhiteSpace(req.Property))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "tenant, property and a valid email are required.");

        var site = await ResolvePropertyAsync(db, req.Tenant, req.Property, ct);
        if (site is null) return accepted;

        db.Scope.Set(site.Value.Tenant, [site.Value.Property], site.Value.Property, null, ActorType.Guest, ctx.CorrelationId);
        await db.ApplyScopeAsync(ct);

        var hash = protector.LookupHash(email, ContactValues.LookupPurpose("Email"));
        var contact = await db.Set<GuestContactPointRow>().AsNoTracking()
            .Where(c => c.ContactType == "Email" && c.LookupHash == hash && c.Status == GuestContactPointStatuses.Active && c.VerifiedAt != null)
            .OrderByDescending(c => c.IsPrimary)
            .FirstOrDefaultAsync(ct);
        if (contact is not null)
            await links.IssueAsync(contact.GuestId, contact.GuestContactPointId, MagicLinkPurposes.SignIn, null, null, null, ct);
        return accepted;
    }

    private static async Task<IResult> Redeem(
        HttpContext http, SpmsDbContext db, MagicLinkService links, IGuestSessionIssuer sessions, IAuditSink audit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RedeemRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;

        var redeemed = await links.RedeemAsync(req!.Token ?? "", ct);
        if (redeemed is null)
            // One answer for unknown, used, revoked and expired.
            return Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId,
                "This link is no longer valid. Ask for a new one.");

        // Now the tenant is known: scope to it (every property; a guest is tenant-wide).
        db.Scope.Set(redeemed.TenantId, [], null, redeemed.PrincipalId, ActorType.Guest, ctx.CorrelationId);
        await db.ApplyScopeAsync(ct);
        var properties = await db.Set<PropertyRow>().AsNoTracking().Where(p => p.Status == PropertyStatuses.Active)
            .Select(p => p.PropertyId).ToListAsync(ct);
        var guest = await db.Set<GuestRow>().AsNoTracking().SingleAsync(g => g.GuestId == redeemed.GuestId, ct);
        var current = guest.HomePropertyId is { } h && properties.Contains(h) ? h : properties.FirstOrDefault();

        db.Scope.Set(redeemed.TenantId, properties, current, redeemed.PrincipalId, ActorType.Guest, ctx.CorrelationId);
        await db.ApplyScopeAsync(ct);

        await db.Set<PrincipalRow>().Where(p => p.PrincipalId == redeemed.PrincipalId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.LastAuthenticatedAt, DateTimeOffset.UtcNow).SetProperty(p => p.Version, p => p.Version + 1), ct);
        await audit.RecordAsync(new AuditEntry("guest.session.start", "guest", redeemed.GuestId.ToString(),
            Purpose: redeemed.Purpose, TenantWide: true), ct);

        var session = sessions.Issue(new GuestSessionClaims(redeemed.TenantId, redeemed.PrincipalId, redeemed.GuestId, current,
            properties, guest.DisplayAlias ?? guest.PreferredName ?? "Guest", redeemed.Purpose,
            redeemed.ScopeEntityType, redeemed.ScopeEntityId));

        return Results.Json(new
        {
            accessToken = session.AccessToken, tokenType = "Bearer", expiresUtc = session.ExpiresUtc,
            purpose = redeemed.Purpose, scopeEntityType = redeemed.ScopeEntityType, scopeEntityId = redeemed.ScopeEntityId,
            guest = new { displayName = guest.PreferredName ?? guest.DisplayAlias },
        }, Json.Options);
    }

    private static async Task<IResult> Me(HttpContext http, SpmsDbContext db, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestSelf) is { } denied) return denied;
        if (!ctx.IsGuest) return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "A guest session is required.");

        var g = await db.Set<GuestRow>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == ctx.GuestId, ct);
        if (g is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        var contacts = await db.Set<GuestContactPointRow>().AsNoTracking()
            .Where(c => c.GuestId == g.GuestId && c.Status == GuestContactPointStatuses.Active)
            .Select(c => new { c.ContactType, c.DisplayHint, c.IsPrimary, verified = c.VerifiedAt != null })
            .ToListAsync(ct);
        return Results.Json(new
        {
            guestId = g.GuestId, preferredName = g.PreferredName, displayAlias = g.DisplayAlias,
            locale = g.Locale, homePropertyId = g.HomePropertyId, contacts,
            purpose = http.User.FindFirst(SpmsClaims.SessionPurpose)?.Value,
        }, Json.Options);
    }

    private static async Task<(Guid Tenant, Guid Property)?> ResolvePropertyAsync(SpmsDbContext db, string tenant, string property, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("SELECT tenant_id, property_id FROM core.resolve_property(@t, @p)", conn,
            (NpgsqlTransaction)Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction!));
        cmd.Parameters.AddWithValue("t", tenant.Trim());
        cmd.Parameters.AddWithValue("p", property.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetGuid(1)) : null;
    }
}
