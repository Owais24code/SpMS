using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Profiles;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Guest.Endpoints;

/* ------------------------------- wire shapes ------------------------------- */

public sealed record ContactDto(string ContactPointId, string ContactType, string DisplayHint, bool IsPrimary, bool Verified, string Status);

/// <summary>
/// The desk's view of a guest. The date of birth is never served: the desk
/// needs to know whether the guest is a minor (IDN-004 guardian rules), not
/// the date itself. Contact values arrive masked.
/// </summary>
public sealed record GuestDto(
    string GuestId, string? DisplayAlias, string? PreferredName, string? LegalFirstName, string? LegalLastName,
    string? PublicQueueId, string? Locale, string? HomePropertyId, bool? IsMinor, string Status, string? MergedIntoGuestId,
    IReadOnlyDictionary<string, string> Preferences, IReadOnlyList<ContactDto> Contacts, int RowVersion, string ETag)
{
    public static GuestDto From(GuestView v, DateOnly today)
    {
        var g = v.Guest;
        bool? minor = g.BirthDate is { } bd ? bd.AddYears(18) > today : null;
        var prefs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(g.Preferences) ?? [];
        return new(g.GuestId.ToString(), g.DisplayAlias, g.PreferredName, g.LegalFirstName, g.LegalLastName, g.PublicQueueId,
            g.Locale, g.HomePropertyId?.ToString(), minor, g.Status, g.MergedIntoGuestId?.ToString(),
            prefs.Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Key, p => p.Value.GetString()!),
            v.Contacts.Select(c => new ContactDto(c.ContactPointId.ToString(), c.ContactType, c.DisplayHint, c.IsPrimary, c.Verified, c.Status)).ToList(),
            g.Version, $"\"{g.Version}\"");
    }
}

public sealed record GuestSearchHitDto(string GuestId, string? DisplayAlias, string? PreferredName, string? LegalLastName,
    string? PublicQueueId, IReadOnlyList<ContactDto> Contacts);

public sealed record MergeCaseDto(string MergeCaseId, string SurvivingGuestId, string DuplicateGuestId, decimal Confidence,
    JsonElement MatchSignals, string Status, string? DecisionReason, string? ReviewedBy, string? ReviewedUtc, string? MergedUtc,
    string? SplitUtc, string? ProposedBy, int RowVersion, string ETag)
{
    public static MergeCaseDto From(GuestMergeCaseRow m) => new(m.GuestMergeCaseId.ToString(), m.SurvivingGuestId.ToString(),
        m.DuplicateGuestId.ToString(), m.Confidence, JsonDocument.Parse(m.MatchSignals).RootElement.Clone(), m.Status, m.DecisionReason,
        m.ReviewedBy?.ToString(), m.ReviewedAt?.ToUniversalTime().ToString("O"), m.MergedAt?.ToUniversalTime().ToString("O"),
        m.SplitAt?.ToUniversalTime().ToString("O"), m.CreatedBy?.ToString(), m.Version, $"\"{m.Version}\"");
}

public sealed record DelegationDto(string DelegationId, string GuestId, string? DelegateGuestId, IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string>? PropertyIds, long? FinancialLimitMinor, string? CurrencyCode, string InformationVisibility,
    string EvidenceReference, string EffectiveFromUtc, string? EffectiveToUtc, string Status, string? RevokedUtc, bool Synced, int RowVersion, string ETag)
{
    public static DelegationDto From(DelegatedAuthorityRow d) => new(d.DelegatedAuthorityId.ToString(), d.GuestId.ToString(),
        d.DelegateGuestId?.ToString(), d.AllowedActions, d.PropertyIds?.Select(p => p.ToString()).ToList(), d.FinancialLimitMinor,
        d.CurrencyCode, d.InformationVisibility, d.EvidenceReference, d.EffectiveFrom.ToUniversalTime().ToString("O"),
        d.EffectiveTo?.ToUniversalTime().ToString("O"), d.Status, d.RevokedAt?.ToUniversalTime().ToString("O"),
        d.FgaSyncedAt is not null, d.Version, $"\"{d.Version}\"");
}

/// <summary>Consent without its evidence: the evidence is restricted and read only in an access export.</summary>
public sealed record ConsentDto(string ConsentId, string GuestId, string Purpose, string? Channel, string TemplateId, int TemplateVersion,
    string EffectiveUtc, string? ExpiresUtc, string Status, string? RevokedUtc, int RowVersion, string ETag)
{
    public static ConsentDto From(ConsentRecordRow c) => new(c.ConsentId.ToString(), c.GuestId.ToString(), c.Purpose, c.Channel,
        c.TemplateId, c.TemplateVersion, c.EffectiveAt.ToUniversalTime().ToString("O"), c.ExpiresAt?.ToUniversalTime().ToString("O"),
        c.Status, c.RevokedAt?.ToUniversalTime().ToString("O"), c.Version, $"\"{c.Version}\"");
}

public sealed record PrivacyRequestDto(string PrivacyRequestId, string GuestId, string RequestType, string? Jurisdiction,
    string RequestedUtc, string DueUtc, string Status, IReadOnlyList<string> AllowedTransitions, string? VerifiedUtc,
    string? FulfilledUtc, string? RejectionReason, int RowVersion, string ETag)
{
    public static PrivacyRequestDto From(PrivacyRequestRow p) => new(p.PrivacyRequestId.ToString(), p.GuestId.ToString(), p.RequestType,
        p.Jurisdiction, p.RequestedAt.ToUniversalTime().ToString("O"), p.DueAt.ToUniversalTime().ToString("O"), p.Status,
        PrivacyService.NextFrom(p.Status), p.VerifiedAt?.ToUniversalTime().ToString("O"), p.FulfilledAt?.ToUniversalTime().ToString("O"),
        p.RejectionReason, p.Version, $"\"{p.Version}\"");
}

public sealed record CreateGuestRequest(string? LegalFirstName, string? LegalLastName, string? PreferredName, string? DisplayAlias,
    string? Locale, string? BirthDate, string? Email, string? Mobile, bool? ContactsVerifiedInPerson);
public sealed record UpdateGuestRequest(string? LegalFirstName, string? LegalLastName, string? PreferredName, string? DisplayAlias,
    string? Locale, string? BirthDate, bool? ClearBirthDate, Dictionary<string, string>? Preferences);
public sealed record ProposeMergeRequest(string? SurvivingGuestId, string? DuplicateGuestId, string? Reason);
public sealed record DecideMergeRequest(string? Decision, string? Reason);
public sealed record SplitMergeRequest(string? Reason);
public sealed record GrantDelegationRequest(string? DelegateGuestId, List<string>? AllowedActions, List<string>? PropertyIds,
    long? FinancialLimitMinor, string? CurrencyCode, string? InformationVisibility, string? EvidenceReference, string? EffectiveToUtc);
public sealed record RevokeRequest(string? Reason);
public sealed record RecordConsentRequest(string? Purpose, string? Channel, string? TemplateId, int? TemplateVersion,
    string? Evidence, string? ExpiresUtc, string? GrantedByGuestId);
public sealed record OpenPrivacyRequest(string? RequestType, string? Jurisdiction);
public sealed record PrivacyTransitionRequest(string? To, string? Reason);

/// <summary>
/// Guest profiles (search, create, read, update), contact points, merge and
/// split (IDN-001), delegated authority (IDN-003), consent (§24) and privacy
/// requests (IDN-006). Guests are tenant-wide; the rights are held at the
/// property the operator is working at, except privacy, which is the tenant's.
/// </summary>
public static class GuestProfileEndpoints
{
    public static IEndpointRouteBuilder MapGuestProfiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guests", Search);
        app.MapPost("/guests", Create);
        app.MapGet("/guests/{id:guid}", Get);
        app.MapPatch("/guests/{id:guid}", Update);
        app.MapPost("/guests/{id:guid}/contact-points/{contactId:guid}/retire", RetireContact);

        app.MapGet("/guest-merge-cases", ListMerges);
        app.MapPost("/guest-merge-cases", ProposeMerge);
        app.MapPost("/guest-merge-cases/{id:guid}/decision", DecideMerge);
        app.MapPost("/guest-merge-cases/{id:guid}/execute", ExecuteMerge);
        app.MapPost("/guest-merge-cases/{id:guid}/split", SplitMerge);

        app.MapGet("/guests/{id:guid}/delegations", ListDelegations);
        app.MapPost("/guests/{id:guid}/delegations", GrantDelegation);
        app.MapPost("/delegations/{id:guid}/revoke", RevokeDelegation);

        app.MapGet("/guests/{id:guid}/consents", ListConsents);
        app.MapPost("/guests/{id:guid}/consents", RecordConsent);
        app.MapPost("/consents/{id:guid}/revoke", RevokeConsent);

        app.MapGet("/privacy-requests", ListPrivacy);
        app.MapPost("/guests/{id:guid}/privacy-requests", OpenPrivacy);
        app.MapPost("/privacy-requests/{id:guid}/transitions", TransitionPrivacy);
        app.MapGet("/privacy-requests/{id:guid}/export", ExportPrivacy);
        return app;
    }

    private static IResult Invalid(RequestContext ctx, string detail, string? field = null) =>
        Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, detail,
            extensions: field is null ? null : Problem.Ext("field_violations", new[] { new { field, rule = "invalid" } }));

    private static IResult Json(HttpContext http, object dto, string etag, int status = 200)
    {
        http.Response.Headers.ETag = etag;
        return Results.Json(dto, Spms.Web.Json.Options, statusCode: status);
    }

    private static Task<IResult?> Property(RequestContext ctx, IAccessDecider access, string relation, CancellationToken ct) =>
        Guard.RequireAccessAsync(ctx, access, relation, Fga.Property(ctx.PropertyId), ct: ct);

    private static IResult FromGov<T>(HttpContext http, RequestContext ctx, GovResult<T> r, Func<T, object> dto, Func<T, string> etag, int created = 200) where T : class =>
        r.Outcome switch
        {
            GovOutcome.Ok => Json(http, dto(r.Row!), etag(r.Row!), created),
            GovOutcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail),
            GovOutcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "It changed since you read it.",
                extensions: r.Row is null ? null : Problem.Ext("current", dto(r.Row))),
            GovOutcome.SameReviewer => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
            GovOutcome.Conflict => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
            _ => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, r.Detail),
        };

    private static DateOnly Today(IClock clock) => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    /* -------------------------------- profiles ------------------------------- */

    private static async Task<IResult> Search(HttpContext http, GuestProfileService guests, IAccessDecider access, string? q, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await Property(ctx, access, "can_read_guest_profile", ct) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Invalid(ctx, "q needs at least two characters: a name, an email, a phone number or a queue id.", "q");
        var hits = await guests.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 50), ct);
        return Results.Json(hits.Select(h => new GuestSearchHitDto(h.Guest.GuestId.ToString(), h.Guest.DisplayAlias, h.Guest.PreferredName,
            h.Guest.LegalLastName, h.Guest.PublicQueueId,
            h.Contacts.Select(c => new ContactDto(c.ContactPointId.ToString(), c.ContactType, c.DisplayHint, c.IsPrimary, c.Verified, c.Status)).ToList())).ToList(),
            Spms.Web.Json.Options);
    }

    private static async Task<IResult> Create(HttpContext http, GuestProfileService guests, IAccessDecider access, IClock clock, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_write_guest_profile", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<CreateGuestRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        DateOnly? birth = null;
        if (req!.BirthDate is not null)
        {
            if (!DateOnly.TryParse(req.BirthDate, out var bd) || bd > Today(clock) || bd.Year < 1900) return Invalid(ctx, "birthDate must be a past date.", "birthDate");
            birth = bd;
        }
        var r = await guests.CreateAsync(new NewGuest(req.LegalFirstName, req.LegalLastName, req.PreferredName, req.DisplayAlias, req.Locale,
            birth, req.Email, req.Mobile, req.ContactsVerifiedInPerson ?? false), ct);
        if (r.Outcome != GuestProfileService.Outcome.Ok) return Invalid(ctx, r.Detail ?? "The guest was not accepted.");
        var dto = GuestDto.From(r.View!, Today(clock));
        http.Response.Headers.Location = $"/guests/{dto.GuestId}";
        http.Response.Headers.ETag = dto.ETag;
        return Results.Json(new { guest = dto, possibleDuplicates = r.PossibleDuplicates?.Select(g => g.ToString()).ToArray() ?? [] },
            Spms.Web.Json.Options, statusCode: 201);
    }

    private static async Task<IResult> Get(HttpContext http, GuestProfileService guests, IAccessDecider access, IClock clock, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        var v = await guests.ViewAsync(id, ct);
        if (v is null || v.Guest.Status == GuestStatuses.Erased) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (await Property(ctx, access, "can_read_guest_profile", ct) is { } refused) return refused;
        var dto = GuestDto.From(v, Today(clock));
        return Json(http, dto, dto.ETag);
    }

    private static async Task<IResult> Update(HttpContext http, GuestProfileService guests, IAccessDecider access, IClock clock, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<UpdateGuestRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_write_guest_profile", ct) is { } refused) return refused;
        DateOnly? birth = null;
        if (req!.BirthDate is not null)
        {
            if (!DateOnly.TryParse(req.BirthDate, out var bd) || bd > Today(clock) || bd.Year < 1900) return Invalid(ctx, "birthDate must be a past date.", "birthDate");
            birth = bd;
        }
        var r = await guests.UpdateAsync(id, version, new GuestChanges(req.LegalFirstName, req.LegalLastName, req.PreferredName,
            req.DisplayAlias, req.Locale, birth, req.ClearBirthDate ?? false, req.Preferences), ct);
        return r.Outcome switch
        {
            GuestProfileService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId),
            GuestProfileService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "The guest changed since you read it.",
                extensions: r.View is null ? null : Problem.Ext("current", GuestDto.From(r.View, Today(clock)))),
            GuestProfileService.Outcome.Invalid => Invalid(ctx, r.Detail!, "preferences"),
            _ => Json(http, GuestDto.From(r.View!, Today(clock)), $"\"{r.View!.Guest.Version}\""),
        };
    }

    private static async Task<IResult> RetireContact(HttpContext http, GuestProfileService guests, IAccessDecider access, IClock clock,
        Guid id, Guid contactId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_write_guest_profile", ct) is { } refused) return refused;
        var r = await guests.RetireContactAsync(id, contactId, ct);
        return r.Outcome == GuestProfileService.Outcome.Ok
            ? Json(http, GuestDto.From(r.View!, Today(clock)), $"\"{r.View!.Guest.Version}\"")
            : Problem.From(ApiError.NotFound, ctx.CorrelationId);
    }

    /* --------------------------------- merges -------------------------------- */

    private static async Task<IResult> ListMerges(HttpContext http, GuestMergeService merges, IAccessDecider access, string? status, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_propose_guest_merge", ct) is { } refused) return refused;
        if (status is not null && !GuestMergeCaseStatuses.All.Contains(status)) return Invalid(ctx, "Unknown status.", "status");
        return Results.Json((await merges.ListAsync(status, ct)).Select(MergeCaseDto.From).ToList(), Spms.Web.Json.Options);
    }

    private static async Task<IResult> ProposeMerge(HttpContext http, GuestMergeService merges, IAccessDecider access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_propose_guest_merge", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<ProposeMergeRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (!Guid.TryParse(req!.SurvivingGuestId, out var s) || !Guid.TryParse(req.DuplicateGuestId, out var d))
            return Invalid(ctx, "survivingGuestId and duplicateGuestId are required.");
        return FromGov(http, ctx, await merges.ProposeAsync(s, d, req.Reason, ct), MergeCaseDto.From, m => $"\"{m.Version}\"", 201);
    }

    private static async Task<IResult> DecideMerge(HttpContext http, GuestMergeService merges, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<DecideMergeRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.Decision is not ("Approve" or "Reject")) return Invalid(ctx, "decision must be Approve or Reject.", "decision");
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_approve_guest_merge", ct) is { } refused) return refused;
        return FromGov(http, ctx, await merges.DecideAsync(id, version, req.Decision == "Approve", req.Reason, ct), MergeCaseDto.From, m => $"\"{m.Version}\"");
    }

    private static async Task<IResult> ExecuteMerge(HttpContext http, GuestMergeService merges, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_approve_guest_merge", ct) is { } refused) return refused;
        return FromGov(http, ctx, await merges.ExecuteAsync(id, version, ct), MergeCaseDto.From, m => $"\"{m.Version}\"");
    }

    private static async Task<IResult> SplitMerge(HttpContext http, GuestMergeService merges, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<SplitMergeRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.Reason)) return Invalid(ctx, "A split needs a reason.", "reason");
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_approve_guest_merge", ct) is { } refused) return refused;
        return FromGov(http, ctx, await merges.SplitAsync(id, version, req.Reason!, ct), MergeCaseDto.From, m => $"\"{m.Version}\"");
    }

    /* ------------------------------- delegation ------------------------------ */

    private static async Task<IResult> ListDelegations(HttpContext http, DelegationService delegations, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await Property(ctx, access, "can_read_guest_profile", ct) is { } refused) return refused;
        return Results.Json((await delegations.ListAsync(id, ct)).Select(DelegationDto.From).ToList(), Spms.Web.Json.Options);
    }

    private static async Task<IResult> GrantDelegation(HttpContext http, DelegationService delegations, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_manage_delegation", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<GrantDelegationRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (!Guid.TryParse(req!.DelegateGuestId, out var delegateId)) return Invalid(ctx, "delegateGuestId is required.", "delegateGuestId");
        if (req.InformationVisibility is not ("ItineraryOnly" or "Standard")) return Invalid(ctx, "informationVisibility must be ItineraryOnly or Standard.", "informationVisibility");
        if (string.IsNullOrWhiteSpace(req.EvidenceReference)) return Invalid(ctx, "evidenceReference is required: how the guest gave this authority.", "evidenceReference");
        if (!Guard.TryParseInstant(req.EffectiveToUtc, out var until)) return Invalid(ctx, "effectiveToUtc is required: every delegation expires.", "effectiveToUtc");
        var properties = new List<Guid>();
        foreach (var p in req.PropertyIds ?? [])
            if (Guid.TryParse(p, out var pid)) properties.Add(pid); else return Invalid(ctx, "propertyIds must be uuids.", "propertyIds");
        var r = await delegations.GrantAsync(id, new NewDelegation(delegateId, req.AllowedActions ?? [], properties, req.FinancialLimitMinor,
            req.CurrencyCode?.Trim().ToUpperInvariant(), req.InformationVisibility, req.EvidenceReference!.Trim(), until), ct);
        return FromGov(http, ctx, r, DelegationDto.From, d => $"\"{d.Version}\"", 201);
    }

    private static async Task<IResult> RevokeDelegation(HttpContext http, DelegationService delegations, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RevokeRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_manage_delegation", ct) is { } refused) return refused;
        return FromGov(http, ctx, await delegations.RevokeAsync(id, version, req!.Reason, ct), DelegationDto.From, d => $"\"{d.Version}\"");
    }

    /* --------------------------------- consent ------------------------------- */

    private static async Task<IResult> ListConsents(HttpContext http, ConsentService consents, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await Property(ctx, access, "can_read_guest_profile", ct) is { } refused) return refused;
        return Results.Json((await consents.ListAsync(id, ct)).Select(ConsentDto.From).ToList(), Spms.Web.Json.Options);
    }

    private static async Task<IResult> RecordConsent(HttpContext http, ConsentService consents, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_write_guest_profile", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RecordConsentRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.Purpose is null || !ConsentPurposes.All.Contains(req.Purpose)) return Invalid(ctx, "Unknown purpose.", "purpose");
        if (req.Channel is not null && !ConsentPurposes.Channels.Contains(req.Channel)) return Invalid(ctx, "Unknown channel.", "channel");
        if (string.IsNullOrWhiteSpace(req.TemplateId) || req.TemplateVersion is null or < 1) return Invalid(ctx, "templateId and templateVersion name the wording the guest agreed to.", "templateId");
        if (string.IsNullOrWhiteSpace(req.Evidence) || req.Evidence.Length > 2000) return Invalid(ctx, "evidence is required (how and where consent was given), up to 2000 characters.", "evidence");
        DateTimeOffset? expires = null;
        if (req.ExpiresUtc is not null) { if (Guard.TryParseInstant(req.ExpiresUtc, out var e)) expires = e; else return Invalid(ctx, "expiresUtc must be an instant.", "expiresUtc"); }
        Guid? grantor = Guid.TryParse(req.GrantedByGuestId, out var gb) ? gb : null;
        var r = await consents.RecordAsync(id, req.Purpose, req.Channel, req.TemplateId!, req.TemplateVersion!.Value, req.Evidence!, expires, grantor, ct);
        return FromGov(http, ctx, r, ConsentDto.From, c => $"\"{c.Version}\"", 201);
    }

    private static async Task<IResult> RevokeConsent(HttpContext http, ConsentService consents, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Property(ctx, access, "can_write_guest_profile", ct) is { } refused) return refused;
        return FromGov(http, ctx, await consents.RevokeAsync(id, version, ct), ConsentDto.From, c => $"\"{c.Version}\"");
    }

    /* --------------------------------- privacy ------------------------------- */

    private static Task<IResult?> Tenant(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        Guard.RequireAccessAsync(ctx, access, "can_handle_privacy_request", Fga.Tenant(ctx.TenantId), ct: ct);

    private static async Task<IResult> ListPrivacy(HttpContext http, PrivacyService privacy, IAccessDecider access, string? status, string? guestId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
        if (await Tenant(ctx, access, ct) is { } refused) return refused;
        if (status is not null && !PrivacyRequestStatuses.All.Contains(status)) return Invalid(ctx, "Unknown status.", "status");
        Guid? guest = Guid.TryParse(guestId, out var g) ? g : null;
        return Results.Json((await privacy.ListAsync(status, guest, ct)).Select(PrivacyRequestDto.From).ToList(), Spms.Web.Json.Options);
    }

    /// <summary>The desk logs a request (spa.guest.write); handling it is the tenant's privacy role.</summary>
    private static async Task<IResult> OpenPrivacy(HttpContext http, PrivacyService privacy, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestWrite) is { } denied) return denied;
        if (await Property(ctx, access, "can_read_guest_profile", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<OpenPrivacyRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.RequestType is null || !PrivacyRequestTypes.All.Contains(req.RequestType)) return Invalid(ctx, "Unknown requestType.", "requestType");
        return FromGov(http, ctx, await privacy.OpenAsync(id, req.RequestType, req.Jurisdiction, ct), PrivacyRequestDto.From, p => $"\"{p.Version}\"", 201);
    }

    private static async Task<IResult> TransitionPrivacy(HttpContext http, PrivacyService privacy, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<PrivacyTransitionRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.To is null || !PrivacyRequestStatuses.All.Contains(req.To)) return Invalid(ctx, "to must be a privacy request status.", "to");
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Tenant(ctx, access, ct) is { } refused) return refused;
        return FromGov(http, ctx, await privacy.TransitionAsync(id, version, req.To, req.Reason, ct), PrivacyRequestDto.From, p => $"\"{p.Version}\"");
    }

    private static async Task<IResult> ExportPrivacy(HttpContext http, PrivacyService privacy, IAccessDecider access, IAuditSink audit, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
        if (await Tenant(ctx, access, ct) is { } refused) return refused;
        var p = await privacy.GetAsync(id, ct);
        if (p is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (p.RequestType is not ("Access" or "Export") || p.Status is not (PrivacyRequestStatuses.Verified or PrivacyRequestStatuses.InProgress or PrivacyRequestStatuses.Fulfilled))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "Only a verified Access or Export request produces an export.");
        var export = await privacy.ExportAsync(p.GuestId, ct);
        await audit.RecordAsync(new AuditEntry("guest.privacy.export", "privacy_request", id.ToString(), p.Version,
            Purpose: "privacy_request", AfterData: new { guestId = p.GuestId, sections = export.Keys }, TenantWide: true), ct);
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"guest-{p.GuestId:N}.json\"";
        return Results.Json(new { privacyRequestId = id, guestId = p.GuestId, generatedUtc = DateTimeOffset.UtcNow, data = export }, Spms.Web.Json.Options);
    }
}
