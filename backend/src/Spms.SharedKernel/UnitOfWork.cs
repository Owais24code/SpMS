namespace Spms.SharedKernel;

/// <summary>
/// A transaction boundary spanning several port writes.
///
/// This exists because the appointment write and the audit row were separate
/// operations with nothing joining them: a failure between the two left the
/// appointment moved with no audit row, and §54.3's evidence trail had a
/// silent hole that nothing detected. A command takes one of these, and its
/// aggregate write, audit row and outbox event either all land or none do.
///
/// Inside an HTTP request the request already owns a scoped transaction (so
/// row-level security has a tenant for every read). A unit of work opened
/// there is a SAVEPOINT: disposing it without commit rolls back to the
/// savepoint, exactly as a top-level transaction would, so a domain service
/// can return early and know its partial work is gone.
///
/// Deliberately NOT in scope: idempotency. Claim and Complete must survive a
/// business rollback, or a refused request would erase the replay record that
/// tells a retry what happened. The idempotency store therefore runs on its
/// own connection, outside any transaction opened here.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Opens a transaction (or a savepoint inside the current one). Disposing
    /// without <see cref="ITransaction.CommitAsync"/> rolls back, so an early
    /// return cannot half-commit.
    /// </summary>
    Task<ITransaction> BeginAsync(CancellationToken ct = default);
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
