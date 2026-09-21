namespace Spms.Domain.Scheduling;

public enum ConflictSeverity { Soft, Hard }

/// <summary>
/// A detected scheduling conflict.
///
/// CON-001 requires every conflict to carry severity, the rule, operational
/// impact AND financial impact, and whether override is permitted — so those
/// are required fields, not optional decoration.
/// </summary>
public sealed record Conflict(
    string Code,
    ConflictSeverity Severity,
    string Summary,
    string OperationalImpact,
    string FinancialImpact,
    IReadOnlyList<string> Resolutions)
{
    /// <summary>
    /// CON-003: a hard conflict is never overridable. Administrator status
    /// alone does not bypass qualification, safety, privacy or capacity.
    /// </summary>
    public bool Overridable => Severity == ConflictSeverity.Soft;
}

public static class ConflictCatalog
{
    public static Conflict ProviderOverlap(string provider) => new(
        "CON-001", ConflictSeverity.Soft,
        $"{provider} is already booked in this window",
        "Two appointments sit with one therapist; the later guest waits.",
        "Likely service recovery on the affected booking.",
        ["Choose another qualified provider", "Move to the next free slot", "Split across two therapists"]);

    public static Conflict ResourceOverlap(string room) => new(
        "CON-002", ConflictSeverity.Hard,
        $"{room} is occupied for this window",
        "A room cannot hold two treatments. Physically impossible.",
        "Both bookings are at risk if committed.",
        ["Use a compatible free room", "Move to a later slot"]);

    public static Conflict ProviderUnqualified(string provider, string service) => new(
        "CON-003", ConflictSeverity.Hard,
        $"{provider} is not qualified for {service}",
        "Delivering this service would be unlicensed.",
        "Regulatory exposure; insurance may not respond.",
        ["Choose a qualified provider", "Change to a service they are licensed for"]);

    public static Conflict BufferCrossed() => new(
        "CON-004", ConflictSeverity.Soft,
        "Duration or turnover crosses blocked time",
        "Turnover is compressed; the next guest may wait.",
        "Minor — possible goodwill discount.",
        ["Extend turnover", "Move to the next free slot"]);

    public static Conflict GuestOverlap(string guest) => new(
        "CON-005", ConflictSeverity.Soft,
        $"{guest} has an overlapping appointment",
        "The guest cannot physically attend both.",
        "One booking becomes a no-show unless acknowledged.",
        ["Move the other booking", "Record an authorised acknowledgment"]);
}
