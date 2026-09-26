using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Spms.SharedKernel;

namespace Spms.Persistence;

/// <summary>
/// IUnitOfWork over the scoped context.
///
/// Top level: a real transaction (scoped by <see cref="ScopeTransactionInterceptor"/>).
/// Nested — inside the request transaction, or inside another unit of work —
/// a SAVEPOINT. The earlier implementation "joined" an outer transaction and
/// made commit and rollback no-ops, so a domain service that returned early
/// after a partial write (consume the preflight token, then find the version
/// stale) left that partial write to be committed by whoever owned the outer
/// transaction. A savepoint gives the inner scope real rollback semantics.
/// </summary>
public sealed class EfUnitOfWork(SpmsDbContext db) : IUnitOfWork
{
    private int _depth;

    public async Task<ITransaction> BeginAsync(CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is { } outer)
        {
            var name = "uow_" + Interlocked.Increment(ref _depth);
            // Flush pending work first: the savepoint must mark the state the
            // caller has already committed to, not a state still in memory.
            await db.SaveChangesAsync(ct);
            await outer.CreateSavepointAsync(name, ct);
            return new Nested(db, outer, name);
        }

        var tx = await db.Database.BeginTransactionAsync(ct);
        return new Owned(db, tx);
    }

    private sealed class Owned(SpmsDbContext db, IDbContextTransaction tx) : ITransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* connection already gone */ }
                // Pending tracked changes belong to the rolled-back attempt. Left
                // in place they would be flushed by the next SaveChanges on this
                // scoped context and resurrect the abandoned write.
                db.ChangeTracker.Clear();
            }
            await tx.DisposeAsync();
        }
    }

    private sealed class Nested(SpmsDbContext db, IDbContextTransaction outer, string savepoint) : ITransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await db.SaveChangesAsync(ct);
            await outer.ReleaseSavepointAsync(savepoint, ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_committed) return;
            // A failed statement aborts the transaction; rolling back to the
            // savepoint is exactly what un-aborts it.
            await outer.RollbackToSavepointAsync(savepoint);
            db.ChangeTracker.Clear();
        }
    }
}
