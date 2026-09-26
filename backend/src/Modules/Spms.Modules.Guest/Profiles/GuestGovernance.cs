using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Identity;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Guest.Profiles;

public enum GovOutcome { Ok, NotFound, StaleVersion, Illegal, Invalid, SameReviewer, Conflict }

public sealed record GovResult<T>(GovOutcome Outcome, T? Row = default, string? Detail = null) where T : class;

/// <summary>
/// IDN-001 merge and split: a reviewed, reversible decision. The proposer may
/// not approve their own case (four eyes, human_reviewed). A merge moves the
/// duplicate's active contact points to the survivor and records which, so a
/// split can move exactly those back; bookings keep the guest they were made
/// for, and new ones are refused for a merged record.
/// </summary>
public sealed class GuestMergeService(SpmsDbContext db, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GovResult<GuestMergeCaseRow>> ProposeAsync(Guid surviving, Guid duplicate, string? reason, CancellationToken ct)
    {
        if (surviving == duplicate) return new(GovOutcome.Invalid, null, "A guest cannot be merged into themselves.");
        await using var tx = await uow.BeginAsync(ct);
        var live = await db.Set<GuestRow>().AsNoTracking()
            .Where(g => (g.GuestId == surviving || g.GuestId == duplicate) && (g.Status == GuestStatuses.Active || g.Status == GuestStatuses.Restricted))
            .CountAsync(ct);
        if (live != 2) return new(GovOutcome.NotFound, null, "Both guests must exist and be active.");
        if (await db.Set<GuestMergeCaseRow>().AnyAsync(m => m.SurvivingGuestId == surviving && m.DuplicateGuestId == duplicate
                && (m.Status == GuestMergeCaseStatuses.Candidate || m.Status == GuestMergeCaseStatuses.Approved), ct))
            return new(GovOutcome.Conflict, null, "An open case already exists for these two guests.");

        var row = new GuestMergeCaseRow
        {
            SurvivingGuestId = surviving, DuplicateGuestId = duplicate,
            MatchSignals = JsonSerializer.Serialize(new { signals = new[] { "manual" }, source = "desk" }, Json),
            Confidence = 1m, DecisionReason = reason, Status = GuestMergeCaseStatuses.Candidate,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await RecordAsync(row, "guest.merge.propose", null, reason, ct);
        await tx.CommitAsync(ct);
        return new(GovOutcome.Ok, Detach(row));
    }

    public Task<List<GuestMergeCaseRow>> ListAsync(string? status, CancellationToken ct)
    {
        var q = db.Set<GuestMergeCaseRow>().AsNoTracking();
        if (status is not null) q = q.Where(m => m.Status == status);
        return q.OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct);
    }

    public Task<GuestMergeCaseRow?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<GuestMergeCaseRow>().AsNoTracking().SingleOrDefaultAsync(m => m.GuestMergeCaseId == id, ct);

    public Task<GovResult<GuestMergeCaseRow>> DecideAsync(Guid id, int version, bool approve, string? reason, CancellationToken ct) =>
        ChangeAsync(id, version, approve ? "guest.merge.approve" : "guest.merge.reject", [GuestMergeCaseStatuses.Candidate], reason, ct, m =>
        {
            // Four eyes: the reviewer is never the proposer (a system-raised candidate has no proposer).
            if (m.CreatedBy is { } proposer && proposer == db.Scope.PrincipalId)
                return Task.FromResult<GovResult<GuestMergeCaseRow>?>(new(GovOutcome.SameReviewer, null, "The person who proposed a merge cannot decide it."));
            m.Status = approve ? GuestMergeCaseStatuses.Approved : GuestMergeCaseStatuses.Rejected;
            m.ReviewedBy = db.Scope.PrincipalId;
            m.ReviewedAt = clock.UtcNow;
            m.DecisionReason = reason ?? m.DecisionReason;
            return Task.FromResult<GovResult<GuestMergeCaseRow>?>(null);
        });

    public Task<GovResult<GuestMergeCaseRow>> ExecuteAsync(Guid id, int version, CancellationToken ct) =>
        ChangeAsync(id, version, "guest.merge.execute", [GuestMergeCaseStatuses.Approved], null, ct, async m =>
        {
            var dup = await db.Set<GuestRow>().SingleAsync(g => g.GuestId == m.DuplicateGuestId, ct);
            var survivor = await db.Set<GuestRow>().AsNoTracking().SingleAsync(g => g.GuestId == m.SurvivingGuestId, ct);
            if (dup.Status is not (GuestStatuses.Active or GuestStatuses.Restricted) || survivor.Status is not (GuestStatuses.Active or GuestStatuses.Restricted))
                return new(GovOutcome.Illegal, null, "Both guests must still be active.");

            var survivorHashes = await db.Set<GuestContactPointRow>().AsNoTracking()
                .Where(c => c.GuestId == m.SurvivingGuestId && c.Status != GuestContactPointStatuses.Retired)
                .Select(c => c.ContactType + ":" + c.LookupHash).ToListAsync(ct);
            var moving = await db.Set<GuestContactPointRow>()
                .Where(c => c.GuestId == m.DuplicateGuestId && c.Status == GuestContactPointStatuses.Active).ToListAsync(ct);
            var moved = new List<Guid>();
            foreach (var c in moving.Where(c => !survivorHashes.Contains(c.ContactType + ":" + c.LookupHash)))
            {
                c.GuestId = m.SurvivingGuestId;
                c.IsPrimary = false;          // the survivor's primaries stay primary
                moved.Add(c.GuestContactPointId);
            }
            dup.Status = GuestStatuses.Merged;
            dup.MergedIntoGuestId = m.SurvivingGuestId;
            m.Status = GuestMergeCaseStatuses.Merged;
            m.MergedAt = clock.UtcNow;
            m.MatchSignals = WithMoved(m.MatchSignals, moved);
            return null;
        });

    public Task<GovResult<GuestMergeCaseRow>> SplitAsync(Guid id, int version, string reason, CancellationToken ct) =>
        ChangeAsync(id, version, "guest.merge.split", [GuestMergeCaseStatuses.Merged], reason, ct, async m =>
        {
            var dup = await db.Set<GuestRow>().SingleAsync(g => g.GuestId == m.DuplicateGuestId, ct);
            var moved = Moved(m.MatchSignals);
            var back = await db.Set<GuestContactPointRow>()
                .Where(c => moved.Contains(c.GuestContactPointId) && c.GuestId == m.SurvivingGuestId).ToListAsync(ct);
            foreach (var c in back) c.GuestId = m.DuplicateGuestId;
            dup.Status = GuestStatuses.Active;
            dup.MergedIntoGuestId = null;
            m.Status = GuestMergeCaseStatuses.Split;
            m.SplitAt = clock.UtcNow;
            m.SplitReason = reason;
            return null;
        });

    private static string WithMoved(string signals, IReadOnlyList<Guid> moved)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(signals) ?? [];
        var o = d.ToDictionary(k => k.Key, v => (object)v.Value);
        o["moved_contact_points"] = moved;
        return JsonSerializer.Serialize(o, Json);
    }

    private static List<Guid> Moved(string signals)
    {
        using var doc = JsonDocument.Parse(signals);
        return doc.RootElement.TryGetProperty("moved_contact_points", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => x.GetGuid()).ToList() : [];
    }

    private async Task<GovResult<GuestMergeCaseRow>> ChangeAsync(Guid id, int version, string action, string[] from, string? reason,
        CancellationToken ct, Func<GuestMergeCaseRow, Task<GovResult<GuestMergeCaseRow>?>> apply)
    {
        await using var tx = await uow.BeginAsync(ct);
        var m = await db.Set<GuestMergeCaseRow>().SingleOrDefaultAsync(x => x.GuestMergeCaseId == id, ct);
        if (m is null) return new(GovOutcome.NotFound);
        if (m.Version != version) { db.ChangeTracker.Clear(); return new(GovOutcome.StaleVersion, await GetAsync(id, ct)); }
        if (!from.Contains(m.Status)) { db.ChangeTracker.Clear(); return new(GovOutcome.Illegal, await GetAsync(id, ct), $"A {m.Status} case cannot do that."); }
        var previous = m.Status;
        if (await apply(m) is { } refused) { db.ChangeTracker.Clear(); return refused; }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(GovOutcome.StaleVersion);
        }
        await RecordAsync(m, action, previous, reason, ct);
        await tx.CommitAsync(ct);
        return new(GovOutcome.Ok, Detach(m));
    }

    private async Task RecordAsync(GuestMergeCaseRow m, string action, string? from, string? reason, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "guest_merge_case", m.GuestMergeCaseId.ToString(), m.Version,
            FromStatus: from, ToStatus: m.Status, ReasonText: reason,
            AfterData: new { surviving = m.SurvivingGuestId, duplicate = m.DuplicateGuestId }, TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.GuestMerged, "guest_merge_case", m.GuestMergeCaseId, m.Version,
            new { mergeCaseId = m.GuestMergeCaseId, surviving = m.SurvivingGuestId, duplicate = m.DuplicateGuestId, from, to = m.Status },
            TenantWide: true));
        await db.SaveChangesAsync(ct);
    }

    private GuestMergeCaseRow Detach(GuestMergeCaseRow m)
    {
        db.ChangeTracker.Clear();
        return m;
    }
}

public static class DelegationActions
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { "Book", "Cancel", "Reschedule", "Pay", "ViewItinerary", "CompleteIntake" };
}

public sealed record NewDelegation(
    Guid DelegateGuestId, IReadOnlyList<string> AllowedActions, IReadOnlyList<Guid>? PropertyIds, long? FinancialLimitMinor,
    string? CurrencyCode, string InformationVisibility, string EvidenceReference, DateTimeOffset EffectiveTo);

/// <summary>
/// IDN-003 delegated authority: explicit actions, properties, financial limit,
/// visibility, expiry and evidence. Mirrored to OpenFGA as conditional tuples
/// (active_delegation) through the outbox; revocation and expiry remove them.
/// There is deliberately no visibility level that includes intake.
/// </summary>
public sealed class DelegationService(SpmsDbContext db, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    public Task<List<DelegatedAuthorityRow>> ListAsync(Guid guestId, CancellationToken ct) =>
        db.Set<DelegatedAuthorityRow>().AsNoTracking().Where(d => d.GuestId == guestId)
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct);

    public async Task<GovResult<DelegatedAuthorityRow>> GrantAsync(Guid guestId, NewDelegation n, CancellationToken ct)
    {
        if (n.DelegateGuestId == guestId) return new(GovOutcome.Invalid, null, "A guest cannot delegate to themselves.");
        if (n.AllowedActions.Count == 0 || n.AllowedActions.Any(a => !DelegationActions.All.Contains(a)))
            return new(GovOutcome.Invalid, null, "allowedActions must be a non-empty subset of Book, Cancel, Reschedule, Pay, ViewItinerary, CompleteIntake.");
        if (n.EffectiveTo <= clock.UtcNow) return new(GovOutcome.Invalid, null, "effectiveTo must be in the future.");
        if ((n.FinancialLimitMinor is null) != (n.CurrencyCode is null)) return new(GovOutcome.Invalid, null, "A financial limit needs a currency, and only a limit has one.");

        await using var tx = await uow.BeginAsync(ct);
        var guest = await db.Set<GuestRow>().AsNoTracking().SingleOrDefaultAsync(g => g.GuestId == guestId, ct);
        var delegateGuest = await db.Set<GuestRow>().SingleOrDefaultAsync(g => g.GuestId == n.DelegateGuestId, ct);
        if (guest is null || delegateGuest is null
            || delegateGuest.Status is not (GuestStatuses.Active or GuestStatuses.Restricted))
            return new(GovOutcome.NotFound, null, "Both the guest and the delegate must be active guests.");

        var delegatePrincipal = await GuestPrincipals.EnsureAsync(db, outbox, delegateGuest, ct);
        var row = new DelegatedAuthorityRow
        {
            GuestId = guestId,
            DelegateGuestId = n.DelegateGuestId,
            AllowedActions = n.AllowedActions.Distinct().ToArray(),
            PropertyIds = n.PropertyIds is { Count: > 0 } ids ? ids.ToArray() : null,
            FinancialLimitMinor = n.FinancialLimitMinor,
            CurrencyCode = n.CurrencyCode,
            InformationVisibility = n.InformationVisibility,
            EvidenceReference = n.EvidenceReference,
            EffectiveFrom = clock.UtcNow,
            EffectiveTo = n.EffectiveTo.ToUniversalTime(),
            Status = DelegatedAuthorityStatuses.Active,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await PublishAsync(row, delegatePrincipal, "guest.delegation.grant", null, null, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    public async Task<GovResult<DelegatedAuthorityRow>> RevokeAsync(Guid id, int version, string? reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<DelegatedAuthorityRow>().SingleOrDefaultAsync(d => d.DelegatedAuthorityId == id, ct);
        if (row is null) return new(GovOutcome.NotFound);
        if (row.Version != version) { db.ChangeTracker.Clear(); return new(GovOutcome.StaleVersion); }
        if (row.Status != DelegatedAuthorityStatuses.Active) { db.ChangeTracker.Clear(); return new(GovOutcome.Illegal, null, $"A {row.Status} delegation cannot be revoked."); }
        row.Status = DelegatedAuthorityStatuses.Revoked;
        row.RevokedAt = clock.UtcNow;
        row.RevokedBy = db.Scope.PrincipalId ?? Guid.Empty;
        await db.SaveChangesAsync(ct);
        await PublishAsync(row, await DelegatePrincipalAsync(row, ct), "guest.delegation.revoke", DelegatedAuthorityStatuses.Active, reason, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    /// <summary>The expiry job: Active past effective_to → Expired, and the tuples go.</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.Set<DelegatedAuthorityRow>()
            .Where(d => d.Status == DelegatedAuthorityStatuses.Active && d.EffectiveTo <= now).Take(200).ToListAsync(ct);
        foreach (var d in due) d.Status = DelegatedAuthorityStatuses.Expired;
        await db.SaveChangesAsync(ct);
        foreach (var d in due)
            await PublishAsync(d, await DelegatePrincipalAsync(d, ct), "guest.delegation.expire", DelegatedAuthorityStatuses.Active, null, ct);
        db.ChangeTracker.Clear();
        return due.Count;
    }

    private async Task<Guid> DelegatePrincipalAsync(DelegatedAuthorityRow d, CancellationToken ct) =>
        d.DelegatePrincipalId
        ?? await db.Set<GuestRow>().AsNoTracking().Where(g => g.GuestId == d.DelegateGuestId).Select(g => g.PrincipalId).SingleAsync(ct)
        ?? Guid.Empty;

    private async Task PublishAsync(DelegatedAuthorityRow d, Guid delegatePrincipal, string action, string? from, string? reason, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "delegated_authority", d.DelegatedAuthorityId.ToString(), d.Version,
            FromStatus: from, ToStatus: d.Status, ReasonText: reason,
            AfterData: new { guestId = d.GuestId, delegateGuestId = d.DelegateGuestId, actions = d.AllowedActions, expires = d.EffectiveTo },
            TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.DelegationChanged, "delegated_authority", d.DelegatedAuthorityId, d.Version, new
        {
            guestId = d.GuestId, delegatePrincipalId = delegatePrincipal, actions = d.AllowedActions, status = d.Status,
            expiresAt = d.EffectiveTo?.ToUniversalTime().ToString("O"),
            propertyIds = d.PropertyIds?.Select(p => p.ToString()).ToArray(),
        }, TenantWide: true));
        await db.SaveChangesAsync(ct);
    }
}

public static class ConsentPurposes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { "Transactional", "Marketing", "HealthIntake", "Photography", "MinorService", "DataSharing", "Profiling" };
    public static readonly IReadOnlySet<string> Channels = new HashSet<string>(StringComparer.Ordinal)
        { "Email", "Sms", "WhatsApp", "Push", "Phone" };
    public const string EvidencePurpose = "guest.consent.evidence";
}

/// <summary>
/// §24 consent: evidence is written once, encrypted, and never edited (the
/// database refuses); revocation is the only later change. An SMS STOP or an
/// unsubscribe is a revocation. Messaging asks <see cref="HasActiveAsync"/>.
/// </summary>
public sealed class ConsentService(SpmsDbContext db, IFieldProtector protector, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    public Task<List<ConsentRecordRow>> ListAsync(Guid guestId, CancellationToken ct) =>
        db.Set<ConsentRecordRow>().AsNoTracking().Where(c => c.GuestId == guestId).OrderByDescending(c => c.EffectiveAt).ToListAsync(ct);

    public async Task<bool> HasActiveAsync(Guid guestId, string purpose, string? channel, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.Set<ConsentRecordRow>().AsNoTracking().AnyAsync(c => c.GuestId == guestId && c.Purpose == purpose
            && c.Status == ConsentRecordStatuses.Active && (c.Channel == null || c.Channel == channel)
            && (c.ExpiresAt == null || c.ExpiresAt > now), ct);
    }

    public async Task<GovResult<ConsentRecordRow>> RecordAsync(Guid guestId, string purpose, string? channel, string templateId,
        int templateVersion, string evidence, DateTimeOffset? expiresUtc, Guid? grantedByGuestId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        if (!await db.Set<GuestRow>().AnyAsync(g => g.GuestId == guestId, ct)) return new(GovOutcome.NotFound);
        var cipher = protector.ProtectString(evidence, ConsentPurposes.EvidencePurpose);
        var row = new ConsentRecordRow
        {
            PropertyId = db.Scope.CurrentPropertyId,
            GuestId = guestId, GrantedByGuestId = grantedByGuestId, Purpose = purpose, Channel = channel,
            TemplateId = templateId, TemplateVersion = templateVersion,
            EvidenceCipher = cipher.Cipher, KeyVersion = cipher.KeyVersion,
            EffectiveAt = clock.UtcNow, ExpiresAt = expiresUtc?.ToUniversalTime(), Status = ConsentRecordStatuses.Active,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await PublishAsync(row, "guest.consent.record", null, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    public async Task<GovResult<ConsentRecordRow>> RevokeAsync(Guid id, int version, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<ConsentRecordRow>().SingleOrDefaultAsync(c => c.ConsentId == id, ct);
        if (row is null) return new(GovOutcome.NotFound);
        if (row.Version != version) { db.ChangeTracker.Clear(); return new(GovOutcome.StaleVersion); }
        if (row.Status != ConsentRecordStatuses.Active) { db.ChangeTracker.Clear(); return new(GovOutcome.Illegal, null, $"A {row.Status} consent cannot be revoked."); }
        row.Status = ConsentRecordStatuses.Revoked;
        row.RevokedAt = clock.UtcNow;
        row.RevokedBy = db.Scope.PrincipalId;
        await db.SaveChangesAsync(ct);
        await PublishAsync(row, "guest.consent.revoke", ConsentRecordStatuses.Active, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    private async Task PublishAsync(ConsentRecordRow c, string action, string? from, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "consent_record", c.ConsentId.ToString(), c.Version,
            Purpose: c.Purpose, FromStatus: from, ToStatus: c.Status,
            AfterData: new { guestId = c.GuestId, c.Purpose, c.Channel, c.TemplateId, c.TemplateVersion }, TenantWide: c.PropertyId is null), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.ConsentChanged, "consent_record", c.ConsentId, c.Version,
            new { consentId = c.ConsentId, guestId = c.GuestId, purpose = c.Purpose, channel = c.Channel, status = c.Status },
            TenantWide: c.PropertyId is null));
        await db.SaveChangesAsync(ct);
    }
}

public static class PrivacyRequestTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { "Access", "Export", "Correction", "Restriction", "Deletion", "ConsentWithdrawal" };
}

/// <summary>
/// IDN-006 privacy requests. Access and Export are answered by
/// <see cref="ExportAsync"/>, which asks every module holding guest data for
/// its part. Deletion is carried out on fulfilment: every module erases its
/// part, the profile is emptied and marked Erased, contact values are
/// destroyed, and audit payloads about the guest are redacted (the audit rows
/// themselves stay — the trail that something happened is kept).
/// </summary>
public sealed class PrivacyService(
    SpmsDbContext db, IFieldProtector protector, IEnumerable<IGuestDataContributor> contributors,
    IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [PrivacyRequestStatuses.Received] = [PrivacyRequestStatuses.Verified, PrivacyRequestStatuses.Rejected, PrivacyRequestStatuses.OnHold],
        [PrivacyRequestStatuses.Verified] = [PrivacyRequestStatuses.InProgress, PrivacyRequestStatuses.Fulfilled, PrivacyRequestStatuses.Rejected, PrivacyRequestStatuses.OnHold],
        [PrivacyRequestStatuses.InProgress] = [PrivacyRequestStatuses.Fulfilled, PrivacyRequestStatuses.OnHold, PrivacyRequestStatuses.Rejected],
        [PrivacyRequestStatuses.OnHold] = [PrivacyRequestStatuses.Verified, PrivacyRequestStatuses.InProgress, PrivacyRequestStatuses.Rejected],
        [PrivacyRequestStatuses.Fulfilled] = [],
        [PrivacyRequestStatuses.Rejected] = [],
    };

    public static IReadOnlyList<string> NextFrom(string s) => Allowed.TryGetValue(s, out var n) ? n : [];

    public Task<List<PrivacyRequestRow>> ListAsync(string? status, Guid? guestId, CancellationToken ct)
    {
        var q = db.Set<PrivacyRequestRow>().AsNoTracking();
        if (status is not null) q = q.Where(p => p.Status == status);
        if (guestId is { } g) q = q.Where(p => p.GuestId == g);
        return q.OrderBy(p => p.DueAt).Take(200).ToListAsync(ct);
    }

    public Task<PrivacyRequestRow?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<PrivacyRequestRow>().AsNoTracking().SingleOrDefaultAsync(p => p.PrivacyRequestId == id, ct);

    public async Task<GovResult<PrivacyRequestRow>> OpenAsync(Guid guestId, string type, string? jurisdiction, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        if (!await db.Set<GuestRow>().AnyAsync(g => g.GuestId == guestId && g.Status != GuestStatuses.Erased, ct)) return new(GovOutcome.NotFound);
        var now = clock.UtcNow;
        var row = new PrivacyRequestRow
        {
            GuestId = guestId, RequestType = type, Jurisdiction = jurisdiction,
            // The statutory clock: 30 days is the common floor; the jurisdiction's own rule is an open decision.
            RequestedAt = now, DueAt = now.AddDays(30), Status = PrivacyRequestStatuses.Received,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await PublishAsync(row, "guest.privacy.open", null, null, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    public async Task<GovResult<PrivacyRequestRow>> TransitionAsync(Guid id, int version, string to, string? reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<PrivacyRequestRow>().SingleOrDefaultAsync(p => p.PrivacyRequestId == id, ct);
        if (row is null) return new(GovOutcome.NotFound);
        if (row.Version != version) { db.ChangeTracker.Clear(); return new(GovOutcome.StaleVersion, await GetAsync(id, ct)); }
        if (!NextFrom(row.Status).Contains(to)) { db.ChangeTracker.Clear(); return new(GovOutcome.Illegal, await GetAsync(id, ct), $"{row.Status} cannot move to {to}."); }
        if (to == PrivacyRequestStatuses.Rejected && string.IsNullOrWhiteSpace(reason))
        { db.ChangeTracker.Clear(); return new(GovOutcome.Invalid, null, "A rejection needs a reason."); }
        if (to == PrivacyRequestStatuses.Fulfilled && row.VerifiedAt is null)
        { db.ChangeTracker.Clear(); return new(GovOutcome.Illegal, null, "The requester's identity must be verified before the request is fulfilled."); }

        var from = row.Status;
        var now = clock.UtcNow;
        row.Status = to;
        if (to == PrivacyRequestStatuses.Verified) { row.VerifiedAt ??= now; row.VerifiedBy ??= db.Scope.PrincipalId; }
        if (to == PrivacyRequestStatuses.Rejected) row.RejectionReason = reason;
        if (to == PrivacyRequestStatuses.Fulfilled) row.FulfilledAt = now;
        await db.SaveChangesAsync(ct);

        var erased = 0;
        if (to == PrivacyRequestStatuses.Fulfilled && row.RequestType == "Deletion") erased = await EraseAsync(row.GuestId, ct);

        await PublishAsync(row, "guest.privacy.transition", from, reason, ct, erased);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(GovOutcome.Ok, row);
    }

    /// <summary>Access / Export: everything held about the guest, from every module, with contact values decrypted.</summary>
    public async Task<Dictionary<string, object?>> ExportAsync(Guid guestId, CancellationToken ct)
    {
        var g = await db.Set<GuestRow>().AsNoTracking().SingleAsync(x => x.GuestId == guestId, ct);
        var contacts = await db.Set<GuestContactPointRow>().AsNoTracking().Where(c => c.GuestId == guestId).ToListAsync(ct);
        var export = new Dictionary<string, object?>
        {
            ["profile"] = new
            {
                g.GuestId, g.LegalFirstName, g.LegalLastName, g.PreferredName, g.DisplayAlias, g.PublicQueueId,
                g.BirthDate, g.Locale, preferences = JsonDocument.Parse(g.Preferences).RootElement, g.Status, g.CreatedAt,
            },
            ["contactPoints"] = contacts.Select(c => new
            {
                c.ContactType, value = c.ContactCipher.Length == 0 ? null : protector.UnprotectString(c.ContactCipher, c.KeyVersion, ContactValues.CipherPurpose),
                c.IsPrimary, verified = c.VerifiedAt != null, c.Status,
            }).ToList(),
            ["consents"] = (await db.Set<ConsentRecordRow>().AsNoTracking().Where(c => c.GuestId == guestId).ToListAsync(ct))
                .Select(c => new { c.Purpose, c.Channel, c.TemplateId, c.TemplateVersion, c.EffectiveAt, c.ExpiresAt, c.Status, c.RevokedAt }).ToList(),
            ["delegations"] = (await db.Set<DelegatedAuthorityRow>().AsNoTracking()
                    .Where(d => d.GuestId == guestId || d.DelegateGuestId == guestId).ToListAsync(ct))
                .Select(d => new { d.GuestId, d.DelegateGuestId, d.AllowedActions, d.EffectiveFrom, d.EffectiveTo, d.Status }).ToList(),
        };
        foreach (var c in contributors) export[c.Section] = await c.ExportAsync(guestId, ct);
        return export;
    }

    private async Task<int> EraseAsync(Guid guestId, CancellationToken ct)
    {
        var changed = 0;
        foreach (var c in contributors) changed += await c.EraseAsync(guestId, ct);

        var principal = db.Scope.PrincipalId;
        changed += await db.Set<GuestRow>().Where(g => g.GuestId == guestId).ExecuteUpdateAsync(u => u
            .SetProperty(g => g.LegalFirstName, (string?)null).SetProperty(g => g.LegalLastName, (string?)null)
            .SetProperty(g => g.PreferredName, (string?)null).SetProperty(g => g.DisplayAlias, "Erased guest")
            .SetProperty(g => g.BirthDate, (DateOnly?)null).SetProperty(g => g.Locale, (string?)null)
            .SetProperty(g => g.Preferences, "{}").SetProperty(g => g.PublicQueueId, (string?)null)
            .SetProperty(g => g.Status, GuestStatuses.Erased)
            .SetProperty(g => g.Version, g => g.Version + 1).SetProperty(g => g.UpdatedBy, principal), ct);

        // The value is destroyed; the row stays as evidence that a contact existed.
        changed += await db.Set<GuestContactPointRow>().Where(c => c.GuestId == guestId).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.ContactCipher, Array.Empty<byte>())
            .SetProperty(c => c.LookupHash, c => "erased:" + c.GuestContactPointId)
            .SetProperty(c => c.DisplayHint, "erased").SetProperty(c => c.IsPrimary, false)
            .SetProperty(c => c.Status, GuestContactPointStatuses.Retired).SetProperty(c => c.SuppressionReason, (string?)null)
            .SetProperty(c => c.Version, c => c.Version + 1).SetProperty(c => c.UpdatedBy, principal), ct);

        changed += await db.Set<GuestMagicLinkRow>().Where(l => l.GuestId == guestId && l.ConsumedAt == null && l.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.RevokedAt, DateTimeOffset.UtcNow), ct);

        // Audit payloads about the guest: only the privacy worker's function may touch them.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction!);
        await using (var cmd = new NpgsqlCommand(
            "SET LOCAL ROLE spms_erasure; SELECT core.redact_audit_subject('guest', @g); SET LOCAL ROLE spms_app;", conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("g", guestId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return changed;
    }

    private async Task PublishAsync(PrivacyRequestRow p, string action, string? from, string? reason, CancellationToken ct, int erased = 0)
    {
        await audit.RecordAsync(new AuditEntry(action, "privacy_request", p.PrivacyRequestId.ToString(), p.Version,
            FromStatus: from, ToStatus: p.Status, ReasonText: reason,
            AfterData: new { guestId = p.GuestId, p.RequestType, p.DueAt, erasedRecords = erased }, TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.PrivacyRequestChanged, "privacy_request", p.PrivacyRequestId, p.Version,
            new { privacyRequestId = p.PrivacyRequestId, guestId = p.GuestId, type = p.RequestType, from, to = p.Status }, TenantWide: true));
        await db.SaveChangesAsync(ct);
    }
}
