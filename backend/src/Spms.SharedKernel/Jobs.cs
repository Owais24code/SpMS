namespace Spms.SharedKernel;

/// <summary>
/// Housekeeping that runs per property on a timer: releasing expired holds,
/// expiring preflight tokens and waitlist offers. The runner resolves a job
/// from a scope whose <see cref="ExecutionScope"/> is already set to one
/// property and whose unit of work is already open, so a job reads and writes
/// exactly as a request at that property would — under RLS, stamped and
/// audited — and commits or rolls back as one.
/// </summary>
public interface IPropertyJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    /// <summary>Returns how many records it changed, for the log.</summary>
    Task<int> RunAsync(CancellationToken ct);
}
