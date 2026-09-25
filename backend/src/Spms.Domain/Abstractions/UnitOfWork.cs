namespace Spms.Domain.Abstractions;

/// <summary>
/// A transaction boundary spanning several port writes.
///
/// This exists because the appointment write and the audit row were separate
/// operations with nothing joining them: a failure between the two left the
/// appointment moved with no audit row, and §54.3's evidence trail had a
/// silent hole that nothing detected. The commit path now takes one of these
/// and the aggregate write, the audit insert and the preflight-token
/// consumption either all land or none do.
///
/// Deliberately NOT in scope: idempotency. Claim and Complete must survive a
/// business rollback, or a refused request would erase the replay record that
/// tells a retry what happened. The idempotency store therefore runs on its
/// own connection, outside any transaction opened here — which is not
/// inferable from the signatures, hence this note.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Opens a transaction. Disposing without <see cref="ITransaction.CommitAsync"/>
    /// rolls back, so an early return cannot half-commit.
    /// </summary>
    Task<ITransaction> BeginAsync(CancellationToken ct = default);
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}

/// <summary>
/// Raised when a write violated the database's own room-overlap constraint.
///
/// CON-002 is enforced by an EXCLUDE constraint rather than by application
/// logic, because the invariant is over the set of appointments sharing a room
/// and no amount of application care survives a second instance. The adapter
/// translates SQLSTATE 23P01 into this so the domain can answer with a
/// conflict instead of letting a driver exception reach the 500 handler.
/// </summary>
public sealed class RoomOverlapException(string? roomId, Exception? inner = null)
    : Exception($"The database refused an overlapping booking for room {roomId ?? "(none)"}.", inner)
{
    public string? RoomId { get; } = roomId;
}
