using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Spms.SharedKernel;

namespace Spms.Persistence;

public sealed class PersistenceOptions
{
    /// <summary>
    /// The role every scoped transaction switches to (SET LOCAL ROLE). spms_app
    /// is NOBYPASSRLS and owns nothing, so even a connection that logged in as
    /// something stronger — the superuser in a test cluster — is subject to
    /// row-level security for the length of the transaction.
    /// </summary>
    public string? RuntimeRole { get; set; } = "spms_app";

    /// <summary>
    /// Refuse to run a command outside a transaction. Outside a transaction
    /// there is no scope, so RLS hides every row: a read would return nothing
    /// and look like an empty day. Failing loudly is the better bug.
    /// </summary>
    public bool RequireScopedTransaction { get; set; } = true;
}

/// <summary>
/// Tenancy, layer 3 of 3, wired in: every transaction the context opens is
/// scoped before its first statement — SET LOCAL ROLE to the runtime role,
/// then core.begin_scope(tenant, properties, principal, correlation). Both are
/// transaction-local, so a pooled connection can never carry one request's
/// tenant into the next.
/// </summary>
public sealed class ScopeTransactionInterceptor(ExecutionScope scope, PersistenceOptions options) : DbTransactionInterceptor
{
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        ScopeSql.Apply(connection, result, scope, options.RuntimeRole);
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await ScopeSql.ApplyAsync(connection, result, scope, options.RuntimeRole, cancellationToken);
        return result;
    }
}

/// <summary>The scoping statements, shared with code that runs on its own raw connection (the idempotency store).</summary>
public static class ScopeSql
{
    public static void Apply(DbConnection connection, DbTransaction tx, ExecutionScope scope, string? role)
    {
        using var cmd = Build(connection, tx, scope, role);
        cmd?.ExecuteNonQuery();
    }

    public static async Task ApplyAsync(DbConnection connection, DbTransaction tx, ExecutionScope scope, string? role, CancellationToken ct)
    {
        await using var cmd = Build(connection, tx, scope, role);
        if (cmd is not null) await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand? Build(DbConnection connection, DbTransaction tx, ExecutionScope scope, string? role)
    {
        var sql = new List<string>();
        if (!string.IsNullOrWhiteSpace(role))
        {
            // Role names come from configuration, never from a request; quoted anyway.
            sql.Add($"SET LOCAL ROLE \"{role.Replace("\"", "\"\"")}\"");
        }
        if (scope.TenantId is not null)
            sql.Add("SELECT core.begin_scope(@t, @p, @pr, @c)");
        if (sql.Count == 0) return null;

        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = string.Join(";\n", sql) + ";";
        if (scope.TenantId is { } tenant)
        {
            cmd.Parameters.Add(new NpgsqlParameter("t", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("p", scope.PropertyIds.ToArray()));
            cmd.Parameters.Add(new NpgsqlParameter("pr", (object?)scope.PrincipalId ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter("c", (object?)scope.CorrelationId ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        }
        return cmd;
    }
}

/// <summary>Refuses statements outside a transaction; see <see cref="PersistenceOptions.RequireScopedTransaction"/>.</summary>
public sealed class UnscopedCommandGuard(PersistenceOptions options) : DbCommandInterceptor
{
    private void Check(DbCommand command)
    {
        if (options.RequireScopedTransaction && command.Transaction is null)
            throw new UnscopedCommandException();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Check(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Check(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Check(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Check(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Check(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Check(command); return ValueTask.FromResult(result); }
}

public sealed class UnscopedCommandException() : InvalidOperationException(
    "A database command ran outside a transaction. Every statement must run inside a scoped transaction " +
    "(the request's, or one opened with IUnitOfWork), or row-level security has no tenant and returns nothing.");

/// <summary>
/// Stamps and guards rows on save. Runs for tracked changes; ExecuteUpdate and
/// ExecuteDelete bypass it and must set version and updated_by themselves.
///
///  * tenant_id / property_id default to the scope and may never point outside it
///  * created_by / updated_by / correlation_id come from the scope
///  * a versioned row advances by exactly one (the database refuses anything else)
///  * append-only rows are never updated or deleted
///  * timestamps are normalised to UTC (Npgsql refuses a non-zero offset)
///  * a UUID primary key left empty is minted as UUIDv7
/// </summary>
public sealed class StampingInterceptor(ExecutionScope scope) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added: OnAdded(entry); break;
                case EntityState.Modified: OnModified(entry); break;
                case EntityState.Deleted:
                    if (entry.Entity is IAppendOnly)
                        throw new AppendOnlyViolationException(entry.Metadata.GetTableName() ?? entry.Metadata.Name, "DELETE");
                    break;
            }
        }
    }

    private void OnAdded(EntityEntry entry)
    {
        var e = entry.Entity;

        foreach (var key in entry.Metadata.FindPrimaryKey()?.Properties ?? [])
        {
            if (key.ClrType == typeof(Guid) && entry.Property(key.Name).CurrentValue is Guid g && g == Guid.Empty)
                entry.Property(key.Name).CurrentValue = Uuid7.New();
        }

        if (e is ITenantOwned t)
        {
            if (t.TenantId == Guid.Empty) t.TenantId = scope.RequireTenant();
            else if (scope.TenantId is { } st && t.TenantId != st) throw CrossScope(entry, "tenant");
        }

        switch (e)
        {
            case IPropertyOwned p:
                if (p.PropertyId == Guid.Empty) p.PropertyId = scope.RequireProperty();
                else if (scope.IsSet && !scope.PropertyIds.Contains(p.PropertyId)) throw CrossScope(entry, "property");
                break;
            case IOptionalPropertyOwned op when op.PropertyId is { } pid:
                if (scope.IsSet && !scope.PropertyIds.Contains(pid)) throw CrossScope(entry, "property");
                break;
        }

        if (e is ICreatedBy c) c.CreatedBy ??= scope.PrincipalId;
        if (e is IUpdatedBy u) u.UpdatedBy = scope.PrincipalId;
        if (e is ICorrelated k) k.CorrelationId ??= scope.CorrelationId;
        NormaliseTimes(entry);
    }

    private void OnModified(EntityEntry entry)
    {
        var e = entry.Entity;
        if (e is IAppendOnly)
            throw new AppendOnlyViolationException(entry.Metadata.GetTableName() ?? entry.Metadata.Name, "UPDATE");

        foreach (var name in new[] { nameof(ITenantOwned.TenantId), nameof(IPropertyOwned.PropertyId) })
        {
            var p = entry.Metadata.FindProperty(name);
            if (p is not null && entry.Property(name).IsModified
                && !Equals(entry.Property(name).OriginalValue, entry.Property(name).CurrentValue))
                throw CrossScope(entry, name == nameof(ITenantOwned.TenantId) ? "tenant" : "property");
        }

        if (e is IVersioned v)
        {
            var original = (int)entry.Property(nameof(IVersioned.Version)).OriginalValue!;
            if (v.Version == original) v.Version = original + 1;
            else if (v.Version != original + 1)
                throw new InvalidOperationException(
                    $"{entry.Metadata.GetTableName()}: version must advance from {original} to {original + 1}, not {v.Version}.");
        }

        if (e is IUpdatedBy u) u.UpdatedBy = scope.PrincipalId;
        if (e is ICorrelated k && scope.CorrelationId is not null) k.CorrelationId = scope.CorrelationId;
        NormaliseTimes(entry);
    }

    private static void NormaliseTimes(EntityEntry entry)
    {
        foreach (var p in entry.Properties)
        {
            if (p.CurrentValue is DateTimeOffset d && d.Offset != TimeSpan.Zero) p.CurrentValue = d.ToUniversalTime();
        }
    }

    private static InvalidOperationException CrossScope(EntityEntry entry, string what) =>
        new CrossScopeWriteException(entry.Metadata.GetTableName() ?? entry.Metadata.Name, what);
}

public sealed class CrossScopeWriteException(string table, string what)
    : InvalidOperationException($"Refused a write to {table}: its {what} is outside the execution scope.");

public sealed class AppendOnlyViolationException(string table, string op)
    : InvalidOperationException($"{table} is append-only ({op} refused).");
