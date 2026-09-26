using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Identity;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Guest.Profiles;

public sealed record ContactView(Guid ContactPointId, string ContactType, string DisplayHint, bool IsPrimary, bool Verified, string Status);

public sealed record GuestView(GuestRow Guest, IReadOnlyList<ContactView> Contacts);

public sealed record NewGuest(
    string? LegalFirstName, string? LegalLastName, string? PreferredName, string? DisplayAlias, string? Locale,
    DateOnly? BirthDate, string? Email, string? Mobile, bool ContactsVerifiedInPerson);

public sealed record GuestChanges(
    string? LegalFirstName, string? LegalLastName, string? PreferredName, string? DisplayAlias, string? Locale,
    DateOnly? BirthDate, bool ClearBirthDate, IReadOnlyDictionary<string, string>? Preferences);

/// <summary>
/// Operational preferences only (pressure, music, provider gender…). The
/// column refuses health data by construction: any key outside this list is
/// rejected, because a free-text "notes" preference is where an allergy
/// would be typed — and that belongs to intake, encrypted, not here.
/// </summary>
public static class GuestPreferences
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>?> Allowed = new Dictionary<string, IReadOnlySet<string>?>
    {
        ["pressure"] = new HashSet<string> { "Light", "Medium", "Firm", "Deep" },
        ["music"] = new HashSet<string> { "None", "Soft", "Nature", "Classical", "GuestChoice" },
        ["providerGender"] = new HashSet<string> { "NoPreference", "Female", "Male" },
        ["roomTemperature"] = new HashSet<string> { "Cool", "Neutral", "Warm" },
        ["robeSize"] = new HashSet<string> { "XS", "S", "M", "L", "XL", "XXL" },
        ["slipperSize"] = null,
        ["language"] = null,
    };

    /// <summary>The first key or value that is not allowed, or null when all are.</summary>
    public static string? Violation(IReadOnlyDictionary<string, string> prefs)
    {
        foreach (var (k, v) in prefs)
        {
            if (!Allowed.TryGetValue(k, out var values)) return k;
            if (values is null ? v.Length is 0 or > 10 : !values.Contains(v)) return k;
        }
        return null;
    }
}

/// <summary>
/// The guest profile (IDN-001/002/005): one person across the tenant, found by
/// name, email or phone, shown by a privacy alias. Never merged silently: a
/// likely duplicate at creation becomes a merge candidate for a manager.
/// </summary>
public sealed class GuestProfileService(SpmsDbContext db, IFieldProtector protector, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Live = [GuestStatuses.Active, GuestStatuses.Restricted];

    public enum Outcome { Ok, NotFound, StaleVersion, Invalid, Duplicate }

    public sealed record Result(Outcome Outcome, GuestView? View = null, string? Detail = null, IReadOnlyList<Guid>? PossibleDuplicates = null);

    public async Task<GuestView?> ViewAsync(Guid guestId, CancellationToken ct)
    {
        var g = await db.Set<GuestRow>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guestId, ct);
        if (g is null) return null;
        var contacts = await db.Set<GuestContactPointRow>().AsNoTracking()
            .Where(c => c.GuestId == guestId && c.Status != GuestContactPointStatuses.Retired)
            .OrderBy(c => c.ContactType).ThenByDescending(c => c.IsPrimary)
            .Select(c => new ContactView(c.GuestContactPointId, c.ContactType, c.DisplayHint, c.IsPrimary, c.VerifiedAt != null, c.Status))
            .ToListAsync(ct);
        return new GuestView(g, contacts);
    }

    /// <summary>
    /// UX-002 universal search: an email or a phone number is matched exactly
    /// through its keyed hash (never decrypted to search); anything else is a
    /// name, alias or queue id. Merged and erased guests are not found.
    /// </summary>
    public async Task<IReadOnlyList<GuestView>> SearchAsync(string q, int limit, CancellationToken ct)
    {
        q = q.Trim();
        List<Guid> ids;
        var digits = new string(q.Where(char.IsDigit).ToArray());
        if (q.Contains('@') && ContactValues.Normalise("Email", q) is { } email)
        {
            var hash = protector.LookupHash(email, ContactValues.LookupPurpose("Email"));
            ids = await ContactMatches("Email", hash, ct);
        }
        else if (digits.Length >= 7 && digits.Length >= q.Count(char.IsLetterOrDigit) - 1)
        {
            ids = [];
            foreach (var type in new[] { "Mobile", "Phone" })
                if (ContactValues.Normalise(type, q) is { } phone)
                    ids.AddRange(await ContactMatches(type, protector.LookupHash(phone, ContactValues.LookupPurpose(type)), ct));
        }
        else
        {
            var like = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            ids = await db.Set<GuestRow>().AsNoTracking()
                .Where(g => Live.Contains(g.Status))
                .Where(g => EF.Functions.ILike((g.LegalLastName ?? "") + " " + (g.LegalFirstName ?? "") + " " + (g.PreferredName ?? ""), like)
                            || EF.Functions.ILike((g.LegalFirstName ?? "") + " " + (g.LegalLastName ?? ""), like)
                            || EF.Functions.ILike(g.DisplayAlias ?? "", like)
                            || g.PublicQueueId == q.ToUpperInvariant())
                .OrderBy(g => g.LegalLastName).ThenBy(g => g.LegalFirstName).ThenBy(g => g.GuestId)
                .Select(g => g.GuestId).Take(limit).ToListAsync(ct);
        }

        var views = new List<GuestView>();
        foreach (var id in ids.Distinct().Take(limit))
            if (await ViewAsync(id, ct) is { } v && Live.Contains(v.Guest.Status)) views.Add(v);
        return views;
    }

    private Task<List<Guid>> ContactMatches(string type, string hash, CancellationToken ct) =>
        db.Set<GuestContactPointRow>().AsNoTracking()
            .Where(c => c.ContactType == type && c.LookupHash == hash && c.Status != GuestContactPointStatuses.Retired)
            .Select(c => c.GuestId).Distinct().ToListAsync(ct);

    public async Task<Result> CreateAsync(NewGuest n, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(n.LegalFirstName) && string.IsNullOrWhiteSpace(n.PreferredName))
            return new Result(Outcome.Invalid, Detail: "A first or preferred name is required.");
        string? email = null, mobile = null;
        if (!string.IsNullOrWhiteSpace(n.Email) && (email = ContactValues.Normalise("Email", n.Email)) is null)
            return new Result(Outcome.Invalid, Detail: "email is not a valid address.");
        if (!string.IsNullOrWhiteSpace(n.Mobile) && (mobile = ContactValues.Normalise("Mobile", n.Mobile)) is null)
            return new Result(Outcome.Invalid, Detail: "mobile is not a valid number.");

        await using var tx = await uow.BeginAsync(ct);
        var first = Clean(n.LegalFirstName);
        var last = Clean(n.LegalLastName);
        var preferred = Clean(n.PreferredName);
        var row = new GuestRow
        {
            GuestId = Uuid7.New(),
            LegalFirstName = first,
            LegalLastName = last,
            PreferredName = preferred,
            DisplayAlias = Clean(n.DisplayAlias) ?? AliasFor(preferred ?? first, last),
            PublicQueueId = await NewQueueIdAsync(ct),
            BirthDate = n.BirthDate,
            Locale = Clean(n.Locale),
            HomePropertyId = db.Scope.CurrentPropertyId,
            Status = GuestStatuses.Active,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);

        var duplicates = new List<(Guid Guest, string Signal, decimal Confidence)>();
        foreach (var (type, value) in new[] { ("Email", email), ("Mobile", mobile) })
        {
            if (value is null) continue;
            var (cipher, hash, hint) = ContactValues.Protect(protector, type, value);
            foreach (var other in await ContactMatches(type, hash, ct))
                if (other != row.GuestId) duplicates.Add((other, type.ToLowerInvariant(), 0.9m));
            db.Add(new GuestContactPointRow
            {
                GuestId = row.GuestId, ContactType = type, ContactCipher = cipher.Cipher, KeyVersion = cipher.KeyVersion,
                LookupHash = hash, DisplayHint = hint, IsPrimary = true,
                VerifiedAt = n.ContactsVerifiedInPerson ? clock.UtcNow : null,
            });
        }
        if (first is not null && last is not null && n.BirthDate is { } bd)
        {
            var same = await db.Set<GuestRow>().AsNoTracking()
                .Where(g => g.GuestId != row.GuestId && Live.Contains(g.Status) && g.BirthDate == bd
                            && EF.Functions.ILike(g.LegalFirstName ?? "", first) && EF.Functions.ILike(g.LegalLastName ?? "", last))
                .Select(g => g.GuestId).ToListAsync(ct);
            duplicates.AddRange(same.Select(g => (g, "name_birthdate", 0.7m)));
        }
        await db.SaveChangesAsync(ct);

        // IDN-001: never merge silently. A likely duplicate is a candidate for review.
        foreach (var d in duplicates.GroupBy(d => d.Guest))
        {
            db.Add(new GuestMergeCaseRow
            {
                SurvivingGuestId = d.Key, DuplicateGuestId = row.GuestId,
                MatchSignals = JsonSerializer.Serialize(new { signals = d.Select(x => x.Signal).Distinct().ToArray(), source = "create" }, Json),
                Confidence = d.Max(x => x.Confidence), Status = GuestMergeCaseStatuses.Candidate,
            });
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry("guest.create", "guest", row.GuestId.ToString(), row.Version,
            AfterData: new { contacts = new[] { email is null ? null : "Email", mobile is null ? null : "Mobile" }.OfType<string>(), possibleDuplicates = duplicates.Count },
            TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.GuestProfileChanged, "guest", row.GuestId, row.Version,
            new { guestId = row.GuestId, change = "created" }, TenantWide: true));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var view = await ViewAsync(row.GuestId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view, PossibleDuplicates: duplicates.Select(d => d.Guest).Distinct().ToList());
    }

    public async Task<Result> UpdateAsync(Guid guestId, int expectedVersion, GuestChanges c, CancellationToken ct)
    {
        if (c.Preferences is { } prefs && GuestPreferences.Violation(prefs) is { } bad)
            return new Result(Outcome.Invalid, Detail: $"'{bad}' is not an operational preference. Health information belongs in intake.");

        await using var tx = await uow.BeginAsync(ct);
        var g = await db.Set<GuestRow>().SingleOrDefaultAsync(x => x.GuestId == guestId, ct);
        if (g is null || !Live.Contains(g.Status)) return new Result(Outcome.NotFound);
        if (g.Version != expectedVersion) { db.Entry(g).State = EntityState.Detached; return new Result(Outcome.StaleVersion, await ViewAsync(guestId, ct)); }

        var changed = new List<string>();
        void Set(string name, string? value, Func<string?> get, Action<string?> put)
        {
            if (value is null) return;
            var v = Clean(value);
            if (v != get()) { put(v); changed.Add(name); }
        }
        Set("legal_first_name", c.LegalFirstName, () => g.LegalFirstName, v => g.LegalFirstName = v);
        Set("legal_last_name", c.LegalLastName, () => g.LegalLastName, v => g.LegalLastName = v);
        Set("preferred_name", c.PreferredName, () => g.PreferredName, v => g.PreferredName = v);
        Set("display_alias", c.DisplayAlias, () => g.DisplayAlias, v => g.DisplayAlias = v);
        Set("locale", c.Locale, () => g.Locale, v => g.Locale = v);
        if (c.ClearBirthDate && g.BirthDate is not null) { g.BirthDate = null; changed.Add("birth_date"); }
        else if (c.BirthDate is { } bd && bd != g.BirthDate) { g.BirthDate = bd; changed.Add("birth_date"); }
        if (c.Preferences is not null)
        {
            var json = JsonSerializer.Serialize(c.Preferences, Json);
            if (json != g.Preferences) { g.Preferences = json; changed.Add("preferences"); }
        }

        if (changed.Count == 0)
        {
            db.Entry(g).State = EntityState.Detached;
            var same = await ViewAsync(guestId, ct);
            await tx.CommitAsync(ct);
            return new Result(Outcome.Ok, same);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new Result(Outcome.StaleVersion);
        }
        // The fields that changed, never their values: names and dates of birth stay out of the audit trail.
        await audit.RecordAsync(new AuditEntry("guest.update", "guest", guestId.ToString(), g.Version,
            ChangedFields: changed, TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.GuestProfileChanged, "guest", guestId, g.Version,
            new { guestId, change = "updated", fields = changed }, TenantWide: true));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var view = await ViewAsync(guestId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    public async Task<Result> RetireContactAsync(Guid guestId, Guid contactPointId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var n = await db.Set<GuestContactPointRow>()
            .Where(c => c.GuestContactPointId == contactPointId && c.GuestId == guestId && c.Status != GuestContactPointStatuses.Retired)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, GuestContactPointStatuses.Retired)
                .SetProperty(c => c.IsPrimary, false)
                .SetProperty(c => c.SuppressionReason, (string?)null)
                .SetProperty(c => c.Version, c => c.Version + 1)
                .SetProperty(c => c.UpdatedBy, db.Scope.PrincipalId), ct);
        if (n == 0) return new Result(Outcome.NotFound);
        await audit.RecordAsync(new AuditEntry("guest.contact_point.retire", "guest", guestId.ToString(),
            AfterData: new { contactPointId }, TenantWide: true), ct);
        await db.SaveChangesAsync(ct);
        var view = await ViewAsync(guestId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    /// <summary>IDN-002: "Ava R." — enough for the desk to call out, not a legal name on a screen.</summary>
    public static string AliasFor(string? given, string? family)
    {
        var g = string.IsNullOrWhiteSpace(given) ? "Guest" : given.Trim();
        return string.IsNullOrWhiteSpace(family) ? g : $"{g} {char.ToUpperInvariant(family.Trim()[0])}.";
    }

    /// <summary>IDN-002 public queue id: short, unambiguous letters and digits, unique per tenant.</summary>
    private async Task<string> NewQueueIdAsync(CancellationToken ct)
    {
        const string alphabet = "ACDEFGHJKLMNPQRTUVWXY34679";
        for (var i = 0; i < 8; i++)
        {
            var id = "G" + new string(Enumerable.Range(0, 5).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
            if (!await db.Set<GuestRow>().IgnoreQueryFilters().AnyAsync(g => g.TenantId == db.Scope.RequireTenant() && g.PublicQueueId == id, ct))
                return id;
        }
        throw new InvalidOperationException("Could not allocate a public queue id.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
