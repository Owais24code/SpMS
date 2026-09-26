using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.SharedKernel;

namespace Spms.Persistence;

public enum EditOutcome { Ok, NotFound, StaleVersion, Illegal, Invalid, Conflict }

public sealed record Edit<T>(EditOutcome Outcome, T? Row = default, string? Detail = null) where T : class
{
    public static Edit<T> Refused(EditOutcome outcome, string? detail) => new(outcome, null, detail);
}

/// <summary>
/// The create and versioned-change path every reference-data service shares:
/// one transaction, the expected version checked before the change and by the
/// database again on write, the database's own rules (unique codes, overlap
/// exclusions, checks) turned into outcomes rather than 500s, and an audit
/// entry written in the same transaction. The returned row is detached and
/// carries the version the write produced.
/// </summary>
public sealed class MasterData(SpmsDbContext db, IAuditSink audit, IUnitOfWork uow)
{
    public async Task<Edit<T>> CreateAsync<T>(T row, string action, string entity, Func<T, Guid> id, CancellationToken ct,
        Func<Task<string?>>? validate = null, Func<T, object?>? after = null) where T : class
    {
        await using var tx = await uow.BeginAsync(ct);
        if (validate is not null && await validate() is { } invalid) return Edit<T>.Refused(EditOutcome.Invalid, invalid);
        db.Add(row);
        if (await SaveAsync(ct) is { } refused) { db.ChangeTracker.Clear(); return Edit<T>.Refused(refused.Outcome, refused.Detail); }
        await audit.RecordAsync(new AuditEntry(action, entity, id(row).ToString(), VersionOf(row),
            ToStatus: StatusOf(row), AfterData: after is null ? row : after(row)), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(EditOutcome.Ok, row);
    }

    /// <summary>
    /// Loads the row, checks the version, applies the change (which may refuse
    /// with a reason: the change is then not made), and saves.
    /// </summary>
    public async Task<Edit<T>> ChangeAwaitAsync<T>(Expression<Func<T, bool>> find, int expectedVersion, string action, string entity,
        Func<T, Guid> id, Func<T, Task<string?>> apply, CancellationToken ct, string? reason = null, Func<T, object?>? after = null) where T : class, IVersioned
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<T>().SingleOrDefaultAsync(find, ct);
        if (row is null) return Edit<T>.Refused(EditOutcome.NotFound, null);
        if (row.Version != expectedVersion)
        {
            db.ChangeTracker.Clear();
            return new(EditOutcome.StaleVersion, row);
        }
        var before = StatusOf(row);
        if (await apply(row) is { } illegal)
        {
            db.ChangeTracker.Clear();
            return new(EditOutcome.Illegal, row, illegal);
        }
        if (await SaveAsync(ct) is { } refused) { db.ChangeTracker.Clear(); return Edit<T>.Refused(refused.Outcome, refused.Detail); }
        await audit.RecordAsync(new AuditEntry(action, entity, id(row).ToString(), row.Version,
            FromStatus: before, ToStatus: StatusOf(row), ReasonText: reason, AfterData: after is null ? row : after(row)), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return new(EditOutcome.Ok, row);
    }

    public Task<Edit<T>> ChangeAsync<T>(Expression<Func<T, bool>> find, int expectedVersion, string action, string entity,
        Func<T, Guid> id, Func<T, string?> apply, CancellationToken ct, string? reason = null, Func<T, object?>? after = null) where T : class, IVersioned =>
        ChangeAwaitAsync(find, expectedVersion, action, entity, id, r => Task.FromResult(apply(r)), ct, reason, after);

    private async Task<(EditOutcome Outcome, string Detail)?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return (EditOutcome.StaleVersion, "The record changed since you read it.");
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException p && Map(p) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>The database's own refusals, as outcomes. Anything else is a fault and propagates.</summary>
    public static (EditOutcome Outcome, string Detail)? Map(PostgresException p) => p.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => (EditOutcome.Conflict, $"That code is already in use ({p.ConstraintName})."),
        PostgresErrorCodes.ExclusionViolation => (EditOutcome.Conflict, $"That overlaps an existing record ({p.ConstraintName})."),
        PostgresErrorCodes.CheckViolation => (EditOutcome.Invalid, $"A rule on the record refused the value ({p.ConstraintName})."),
        PostgresErrorCodes.ForeignKeyViolation => (EditOutcome.Invalid, $"A referenced record does not exist ({p.ConstraintName})."),
        PostgresErrorCodes.NotNullViolation => (EditOutcome.Invalid, $"{p.ColumnName} is required."),
        PostgresErrorCodes.RaiseException => (EditOutcome.Illegal, p.MessageText),
        _ => null,
    };

    /// <summary>For services that write outside Create/Change but keep the same trail.</summary>
    public Task AuditAsync(AuditEntry entry, CancellationToken ct) => audit.RecordAsync(entry, ct);

    private string? StatusOf(object row) =>
        db.Model.FindEntityType(row.GetType())?.FindProperty("Status") is { } p ? p.PropertyInfo?.GetValue(row) as string : null;

    private int? VersionOf(object row) => row is IVersioned v ? v.Version : null;
}
