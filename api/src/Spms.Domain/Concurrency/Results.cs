using Spms.Domain.Errors;
using Spms.Domain.Scheduling;

namespace Spms.Domain.Concurrency;

/// <summary>
/// The outcome of a write.
///
/// Stale deliberately carries the current server state so the caller can show
/// the user what changed underneath them and keep their unsaved edit — the
/// spec requires a recoverable error to preserve unsaved work, which a bare
/// 412 cannot do.
/// </summary>
public abstract record WriteOutcome<T>
{
    public sealed record Committed(T Value, int NewVersion) : WriteOutcome<T>;

    public sealed record Stale(T Current, int CurrentVersion) : WriteOutcome<T>
    {
        public SpmsProblem Problem => SpmsProblem.StaleVersion;
    }

    public sealed record Conflicted(IReadOnlyList<Conflict> Conflicts) : WriteOutcome<T>
    {
        /// <summary>Hard conflicts win: one non-overridable entry blocks the commit.</summary>
        public bool AnyHard => Conflicts.Any(c => !c.Overridable);

        public SpmsProblem Problem => AnyHard
            ? SpmsProblem.HardConflict
            : SpmsProblem.SoftConflictApprovalRequired;
    }

    public sealed record Rejected(SpmsProblem Problem, string? Detail = null) : WriteOutcome<T>;
}
