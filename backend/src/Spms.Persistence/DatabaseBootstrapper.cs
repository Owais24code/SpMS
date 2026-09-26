using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Spms.SharedKernel;

namespace Spms.Persistence;

/// <summary>
/// Brings a database to the current schema.
///
///  1. Roles (cluster-level, idempotent) with an administrative connection.
///  2. EF Core migrations as spms_owner. The R1 baseline migration IS the
///     reviewed SQL in database/schema; later migrations are ordinary EF
///     migrations, with anything EF cannot model (exclusions, triggers, RLS,
///     SECURITY DEFINER functions) written as migrationBuilder.Sql().
///
/// Deployed environments run this from the pipeline (the Host's --migrate
/// switch), never from app start-up: two instances starting together would
/// race, and a schema change is not an instance's decision to make.
/// </summary>
public static class DatabaseBootstrapper
{
    public const string HistorySchema = "spms_migrations";
    public const string HistoryTable = "__ef_migrations_history";

    public static async Task EnsureRolesAsync(string adminConnectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct);
        await using (var roles = new NpgsqlCommand(SchemaScripts.Roles(), conn))
            await roles.ExecuteNonQueryAsync(ct);

        await using var grant = new NpgsqlCommand(
            $"GRANT CREATE ON DATABASE \"{conn.Database.Replace("\"", "\"\"")}\" TO spms_owner", conn);
        await grant.ExecuteNonQueryAsync(ct);
    }

    public static async Task<IReadOnlyList<string>> MigrateAsync(
        string ownerConnectionString, IEnumerable<IModelContributor> contributors,
        string? migrationRole = "spms_owner", CancellationToken ct = default)
    {
        await using var db = CreateMigrationContext(ownerConnectionString, contributors, migrationRole);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        await db.Database.MigrateAsync(ct);
        return pending;
    }

    public static SpmsDbContext CreateMigrationContext(
        string connectionString, IEnumerable<IModelContributor> contributors, string? migrationRole)
    {
        var options = new DbContextOptionsBuilder<SpmsDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
                npg => npg.MigrationsHistoryTable(HistoryTable, HistorySchema))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .AddInterceptors(new SetRoleOnOpen(migrationRole))
            .Options;
        return new SpmsDbContext(options, new ExecutionScope(), contributors);
    }

    /// <summary>Session-level role for a dedicated, unpooled migration connection only.</summary>
    private sealed class SetRoleOnOpen(string? role) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (string.IsNullOrWhiteSpace(role)) return;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET ROLE \"{role.Replace("\"", "\"\"")}\"";
            cmd.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(role)) return;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET ROLE \"{role.Replace("\"", "\"\"")}\"";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
