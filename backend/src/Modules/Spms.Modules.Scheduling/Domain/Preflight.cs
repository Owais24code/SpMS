namespace Spms.Modules.Scheduling.Domain;

/// <summary>A proposed change, not yet committed (SCH-020 / GUI-003).</summary>
public sealed record MoveProposal(
    string AppointmentId,
    DateTimeOffset StartUtc,
    string? ProviderId,
    string? RoomId,
    int FromRowVersion);

/// <summary>
/// The result of validating a proposal. The token is single use and expires.
///
/// The conflict list here is a record of WHAT THE OPERATOR WAS SHOWN, not a
/// warrant to skip validation: the commit path re-evaluates the board and
/// refuses if a hard conflict has appeared since. Treating this snapshot as
/// authoritative let a room booked during the token's lifetime go unnoticed.
/// </summary>
public sealed record PreflightResult(
    string Token,
    DateTimeOffset ExpiresUtc,
    MoveProposal Proposal,
    string PropertyId,
    DateTimeOffset ProposedStartUtc,
    DateTimeOffset ProposedEndUtc,
    IReadOnlyList<Conflict> Conflicts)
{
    public bool CommitAllowed => Conflicts.All(c => c.Overridable);

    /// <summary>
    /// Only meaningful when a commit is possible at all. Reporting
    /// RequiresReason alongside CommitAllowed=false showed the operator a
    /// reason box that could never succeed.
    /// </summary>
    public bool RequiresReason => CommitAllowed && Conflicts.Count > 0;
    public bool IsExpired(DateTimeOffset now) => now > ExpiresUtc;
}
