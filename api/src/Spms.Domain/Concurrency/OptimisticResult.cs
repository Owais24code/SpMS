using Spms.Domain.Errors;

namespace Spms.Domain.Concurrency;

/// <summary>
/// Every consequential edit in SpMS carries the version the client last read.
/// </summary>
/// <param name="Value">The edit payload.</param>
/// <param name="ExpectedVersion">
/// Row version / ETag the client based this edit on. Required — an edit with
/// no version is rejected with <see cref="SpmsCode.VersionMissing"/> rather
/// than silently treated as last-write-wins.
/// </param>
public readonly record struct VersionedEdit<T>(T Value, byte[] ExpectedVersion);

/// <summary>
/// The outcome of a versioned write.
///
/// A stale result deliberately carries BOTH the caller's rejected payload and
/// the current server state. The UX spec requires that a recoverable error
/// preserves unsaved user work, so the API hands the client everything it
/// needs to re-present the user's edit alongside what changed underneath —
/// rather than returning a bare 409 and losing the user's typing.
/// </summary>
public abstract record OptimisticResult<T>
{
    public sealed record Committed(T Value, byte[] NewVersion) : OptimisticResult<T>;

    public sealed record Stale(
        T RejectedValue,
        T CurrentValue,
        byte[] CurrentVersion,
        string Code = SpmsCode.StaleVersion) : OptimisticResult<T>;

    public sealed record Conflict(
        string Code,
        ConflictSeverity Severity,
        string Consequence,
        IReadOnlyList<string> Alternatives,
        T RejectedValue) : OptimisticResult<T>
    {
        /// <summary>
        /// Hard conflicts are never overridable. The API exposes this so the
        /// UI can render "blocked" rather than offering an override path the
        /// server would refuse anyway.
        /// </summary>
        public bool IsOverridable => Severity == ConflictSeverity.Soft;
    }

    public sealed record Denied(string Code, string SafeMessage) : OptimisticResult<T>;
}
