using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Endpoints;

/* Wire shapes. Kept separate from the domain so a field can be added to the
   aggregate without leaking to clients, and so restricted fields are omitted
   at serialization (SEC-020) rather than filtered in the UI.

   GuestId is deliberately absent from every response below: the board needs
   the alias to render and the id only ever travels server-side. */

public sealed record AppointmentDto(
    string AppointmentId,
    string GuestAlias,
    string ServiceId,
    string ServiceName,
    int DurationMinutes,
    string? ProviderId,
    string? RoomId,
    string StartUtc,
    string EndUtc,
    string StartLocal,
    string PropertyTimeZone,
    string Status,
    bool Reschedulable,
    IReadOnlyList<string> AllowedTransitions,
    int RowVersion,
    string ETag,
    string? ConfirmationNumber)
{
    public static AppointmentDto From(Appointment a) => new(
        a.AppointmentId, a.GuestAlias, a.ServiceId, a.ServiceName, a.DurationMinutes,
        a.ProviderId, a.RoomId,
        // ToUniversalTime before formatting: a field named StartUtc that
        // echoes back whatever offset the caller sent is not a UTC field.
        a.StartUtc.ToUniversalTime().ToString("O"),
        a.EndUtc.ToUniversalTime().ToString("O"),
        LocalClock.Label(a.StartUtc, a.PropertyTimeZone), a.PropertyTimeZone,
        a.Status.ToString(), a.IsReschedulable,
        AppointmentTransitions.NextFrom(a.Status).Select(s => s.ToString()).ToList(),
        a.RowVersion,
        // ETag formatting is an HTTP concern, so it lives on the DTO rather
        // than on the aggregate.
        $"\"{a.RowVersion}\"",
        a.ConfirmationNumber);
}

/// <summary>
/// Property-local rendering. The board is always read in the property's zone,
/// never the viewer's — a scheduler in one city routinely works another.
/// </summary>
public static class LocalClock
{
    private static readonly Dictionary<string, TimeZoneInfo?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Resolves and memoizes a zone. FindSystemTimeZoneById was called once
    /// per row per request, up to the page limit.
    /// </summary>
    public static TimeZoneInfo? Resolve(string timeZoneId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(timeZoneId, out var cached)) return cached;

            TimeZoneInfo? tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // A misconfigured property must not take the board down.
                tz = null;
            }

            Cache[timeZoneId] = tz;
            return tz;
        }
    }

    /// <summary>
    /// The instant at which a calendar day begins at the property.
    ///
    /// Both /availability and /appointments?date= answer a question about a
    /// business day, so both have to mean the same day. They did not: the grid
    /// was built in the property's zone while the list was built from UTC
    /// midnight, so a property at a large positive offset lost its morning
    /// from the board while the grid still showed it. One function, called by
    /// both, is what keeps that from drifting apart again.
    ///
    /// A date that does not exist locally (spring-forward) is nudged past the
    /// gap rather than throwing on one day a year, and an unresolvable zone
    /// falls back to UTC rather than taking the board down.
    /// </summary>
    public static DateTimeOffset DayStartUtc(DateOnly day, string timeZoneId)
    {
        var tz = Resolve(timeZoneId);
        var localMidnight = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);

        if (tz is null) return new DateTimeOffset(localMidnight, TimeSpan.Zero);

        if (tz.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddHours(1);
        return new DateTimeOffset(localMidnight, tz.GetUtcOffset(localMidnight)).ToUniversalTime();
    }

    public static string Label(DateTimeOffset instant, string timeZoneId)
    {
        var tz = Resolve(timeZoneId);
        // The Z suffix tells the reader the zone could not be resolved, rather
        // than quietly presenting UTC as local.
        return tz is null
            ? instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + "Z"
            : TimeZoneInfo.ConvertTime(instant, tz).ToString("yyyy-MM-dd HH:mm");
    }
}

/// <summary>
/// Every collection response is a page. An unbounded list is a latent outage:
/// one busy property's year of history would be a single 40MB response.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);

public sealed record CreateAppointmentRequest(
    string? GuestId, string? GuestAlias, string? ServiceId, string? StartUtc,
    string? ProviderId, string? RoomId, string? Reason);

/// <summary>
/// FromRowVersion is required, not defaulted from the current row. Defaulting
/// it made the optimistic check vacuous: the client asserted whatever the
/// server had just read, so a concurrent edit could never be detected.
/// </summary>
public sealed record PreflightRequest(
    string? AppointmentId, string? StartUtc, string? ProviderId, string? RoomId, int? FromRowVersion);

public sealed record ConflictDto(
    string Code, string Rule, string Severity, string Summary,
    string OperationalImpact, string FinancialImpact,
    bool Overridable, IReadOnlyList<string> Resolutions)
{
    public static ConflictDto From(Conflict c) => new(
        // Rule travels alongside the code: room turnover and provider
        // transition are both CON-004, and a client that groups by code alone
        // would collapse two different breaches into one row.
        c.Code, c.Rule, c.Severity.ToString().ToLowerInvariant(), c.Summary,
        c.OperationalImpact, c.FinancialImpact, c.Overridable, c.Resolutions);

    public static List<ConflictDto> FromAll(IEnumerable<Conflict> conflicts) =>
        conflicts.Select(From).ToList();
}

public sealed record PreflightResponse(
    string Token, string ExpiresUtc, int TtlSeconds,
    string AppointmentId, int FromRowVersion,
    string ProposedStartUtc, string ProposedEndUtc,
    bool CommitAllowed, bool RequiresReason, IReadOnlyList<ConflictDto> Conflicts)
{
    public static PreflightResponse From(PreflightResult r, DateTimeOffset nowUtc) => new(
        r.Token, r.ExpiresUtc.ToUniversalTime().ToString("O"),
        // Rounded up: truncation reported 89 on a fresh 90-second token, so a
        // client counting down was consistently a second short.
        (int)Math.Ceiling(Math.Max(0, (r.ExpiresUtc - nowUtc).TotalSeconds)),
        r.Proposal.AppointmentId, r.Proposal.FromRowVersion,
        r.ProposedStartUtc.ToUniversalTime().ToString("O"),
        r.ProposedEndUtc.ToUniversalTime().ToString("O"),
        r.CommitAllowed, r.RequiresReason,
        ConflictDto.FromAll(r.Conflicts));
}

public sealed record ReassignRequest(string? Token, string? Reason);
public sealed record TransitionRequest(string? To, string? Reason);

/// <summary>
/// An availability slot. StartLocal is the property's wall clock, matching
/// AppointmentDto — the two endpoints previously used one field name for two
/// different clocks, so a board rendering slots was hours off.
/// </summary>
public sealed record AvailabilitySlot(
    string StartUtc, string StartLocal, bool Open, IReadOnlyList<string> BusyRooms, IReadOnlyList<string> BusyProviders);

/// <summary>
/// The audit projection. Hashes travel; the hashed values never do (§54.3).
/// </summary>
public sealed record AuditEntryDto(
    string AtUtc, string Actor, string Action, string Purpose,
    string SubjectType, string SubjectId, int SubjectVersion,
    string? BeforeHash, string? AfterHash,
    IReadOnlyList<string> ConflictCodes, string? SelectedResolution, string? TargetStatus,
    string? Reason, string CorrelationId)
{
    public static AuditEntryDto From(AuditEntry e) => new(
        e.AtUtc.ToUniversalTime().ToString("O"), e.Actor, e.Action, e.Purpose,
        e.SubjectType, e.SubjectId, e.SubjectVersion,
        e.BeforeHash, e.AfterHash, e.ConflictCodes, e.SelectedResolution, e.TargetStatus,
        e.Reason, e.CorrelationId);
}
