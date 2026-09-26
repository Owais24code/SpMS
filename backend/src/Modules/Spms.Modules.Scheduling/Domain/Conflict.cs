namespace Spms.Modules.Scheduling.Domain;

public enum ConflictSeverity { Soft, Hard }

/// <summary>
/// A detected conflict. CON-001 requires the popup to state severity, the rule,
/// and both operational and financial impact — so the API returns all of it
/// rather than leaving the client to invent copy.
/// </summary>
public sealed record Conflict(
    string Code,
    /// <summary>
    /// Which rule fired, independent of the wire code. Room turnover and
    /// provider transition are BOTH CON-004 in the register, so deduplicating
    /// by code alone silently discarded one of two different breaches — the
    /// operator was told housekeeping was tight and never told the therapist
    /// had no transition time, and the offered resolutions did not fix it.
    /// </summary>
    string Rule,
    ConflictSeverity Severity,
    string Summary,
    string OperationalImpact,
    string FinancialImpact,
    IReadOnlyList<string> Resolutions)
{
    public bool Overridable => Severity == ConflictSeverity.Soft;
}

/// <summary>The CON-001..CON-007 register from SPECIFICATION_v2.12.md.</summary>
public static class ConflictCatalog
{
    public static Conflict ProviderOverlap(string providerId) => new(
        "CON-001", "provider-overlap", ConflictSeverity.Soft,
        $"Provider {providerId} is already booked in that window",
        "Two appointments would sit with one therapist; the later guest waits.",
        "Likely service recovery on the affected booking.",
        ["Choose another qualified provider", "Move to the next free slot", "Split across two providers"]);

    public static Conflict ResourceOverlap(string roomId) => new(
        "CON-002", "room-overlap", ConflictSeverity.Hard,
        $"Room {roomId} is occupied for that window",
        "A room cannot hold two treatments. This is physically impossible.",
        "Commit would be reversed; both bookings at risk.",
        ["Choose a compatible free room", "Move the appointment"]);

    public static Conflict ProviderNotQualified(string providerId, string serviceName) => new(
        "CON-003", "provider-not-qualified", ConflictSeverity.Hard,
        $"Provider {providerId} is not qualified for {serviceName}",
        "Delivering the service would be unlicensed.",
        "Regulatory exposure; insurance may not respond.",
        ["Choose a qualified provider", "Change to a service they are licensed for"]);

    public static Conflict RoomTurnoverCrossed(int minutes) => new(
        "CON-004", "room-turnover", ConflictSeverity.Soft,
        $"Less than {minutes} minutes of room turnover",
        "Housekeeping cannot reset the room in time; the next guest may wait.",
        "Minor — possible discount on the following booking.",
        ["Extend turnover", "Move to the next free slot", "Use another room"]);

    public static Conflict ProviderTransitionCrossed(int minutes) => new(
        "CON-004", "provider-transition", ConflictSeverity.Soft,
        $"Less than {minutes} minutes between treatments for this provider",
        "The therapist has no transition time; the day compounds late.",
        "Minor — overtime risk at the end of shift.",
        ["Move to the next free slot", "Assign another provider"]);

    /// <summary>
    /// Fails closed. An unknown provider is refused rather than assumed
    /// qualified — a new hire or a typo would otherwise pass a licensing rule.
    /// </summary>
    public static Conflict ProviderUnknown(string providerId) => new(
        "CON-003", "provider-unknown", ConflictSeverity.Hard,
        $"Provider {providerId} has no qualification record",
        "We cannot establish that this person is licensed for the service.",
        "Regulatory exposure; insurance may not respond.",
        ["Choose a provider with a current qualification", "Ask HR to record the credential"]);

    public static Conflict GuestOverlap(string guestAlias) => new(
        "CON-005", "guest-overlap", ConflictSeverity.Soft,
        $"{guestAlias} has an overlapping appointment",
        "The guest cannot physically attend both.",
        "One booking becomes a no-show unless acknowledged.",
        ["Move the other booking", "Record an authorised acknowledgment"]);

    /// <summary>CON-005 across properties: the guest is booked at another of the tenant's properties.</summary>
    public static Conflict GuestOverlapElsewhere(string guestAlias) => new(
        "CON-005", "guest-overlap-elsewhere", ConflictSeverity.Soft,
        $"{guestAlias} has an overlapping appointment at another property",
        "The guest cannot physically attend both; one is at a different site.",
        "One booking becomes a no-show unless acknowledged.",
        ["Move this booking", "Contact the other property", "Record an authorised acknowledgment"]);

    /// <summary>The room is out of service (a maintenance window) for part of the interval.</summary>
    public static Conflict RoomOutOfService(string roomId) => new(
        "CON-002", "room-out-of-service", ConflictSeverity.Hard,
        $"Room {roomId} is closed for maintenance in that window",
        "The room cannot be used while it is out of service.",
        "Commit would be reversed; the booking is at risk.",
        ["Choose a compatible free room", "Move the appointment"]);

    /// <summary>The provider is on leave or has no shift covering the interval.</summary>
    public static Conflict ProviderUnavailable(string providerId) => new(
        "CON-001", "provider-unavailable", ConflictSeverity.Soft,
        $"Provider {providerId} is not rostered for that window",
        "Nobody has confirmed this therapist will be on site.",
        "Possible overtime, or a no-show by the provider.",
        ["Choose a rostered provider", "Ask the scheduler to add a shift"]);

    /// <summary>The room does not exist at this property, or is retired.</summary>
    public static Conflict RoomUnknown(string roomId) => new(
        "CON-002", "room-unknown", ConflictSeverity.Hard,
        $"Room {roomId} is not a bookable room at this property",
        "There is no such room to put the guest in.",
        "The booking cannot be delivered as specified.",
        ["Choose one of this property's rooms"]);

    public static Conflict StaleServiceVersion() => new(
        "CON-006", "stale-service-version", ConflictSeverity.Soft,
        "The service price or version has moved on",
        "The quoted price no longer matches the catalogue.",
        "Revenue variance on commit.",
        ["Re-quote at the current price"]);

    public static Conflict DependencyDown(string system) => new(
        "CON-007", "dependency-down", ConflictSeverity.Hard,
        $"{system} is unavailable",
        "The owning system cannot confirm; state would diverge.",
        "Unknown until the dependency responds.",
        ["Retry once the dependency responds"]);
}
