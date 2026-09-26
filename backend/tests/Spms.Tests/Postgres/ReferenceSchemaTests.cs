using Npgsql;
using Spms.Persistence;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// The migrations are frozen SQL; the reference schema (database/schema) keeps
/// evolving from database/model. Built each way, the two databases must be the
/// same object for object — columns, defaults, constraints, indexes, policies,
/// functions (body hash, owner, grants), triggers and table grants. This is the
/// drift check between "what we reviewed" and "what we deploy".
/// </summary>
public class ReferenceSchemaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Schemas =
        "'core','catalog','resources','workforce','guest','scheduling','intake','inventory','commerce','messaging','reporting'";

    [RequiresPostgres]
    public async Task Migrations_build_exactly_the_reference_schema()
    {
        var name = "spms_ref_" + Guid.NewGuid().ToString("n")[..10];
        await AdminAsync($"CREATE DATABASE \"{name}\"");
        var refConn = new NpgsqlConnectionStringBuilder(PostgresFixture.AdminConnectionString) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await DatabaseBootstrapper.EnsureRolesAsync(refConn);
            var dir = Path.Combine(RepoRoot(), "database", "schema");
            await using (var conn = new NpgsqlConnection(refConn))
            {
                await conn.OpenAsync();
                foreach (var file in Directory.GetFiles(dir, "*.sql")
                             .Where(f => !f.EndsWith("000_roles.sql", StringComparison.Ordinal))
                             .Order(StringComparer.Ordinal))
                {
                    await using var cmd = new NpgsqlCommand("SET ROLE spms_owner;\n" + await File.ReadAllTextAsync(file) + "\nRESET ROLE;", conn);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var migrated = await Fingerprint(fixture.ConnectionString);
            var reference = await Fingerprint(refConn);
            var diff = reference.Except(migrated).Select(x => "reference only: " + x)
                .Concat(migrated.Except(reference).Select(x => "migrations only: " + x))
                .Take(20).ToList();
            Assert.Empty(diff);
            Assert.True(migrated.Count > 1000, $"the fingerprint is suspiciously small ({migrated.Count})");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await AdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    private static async Task AdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(PostgresFixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> Fingerprint(string connectionString)
    {
        var sql = $"""
            SELECT 'col ' || n.nspname || '.' || c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
                   || ' ' || a.attnotnull || ' ' || coalesce(pg_get_expr(d.adbin, d.adrelid), '')
              FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
              LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
             WHERE n.nspname IN ({Schemas}) AND c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL
            SELECT 'con ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
              FROM pg_constraint WHERE connamespace::regnamespace::text IN ({Schemas})
            UNION ALL
            SELECT 'idx ' || indexdef FROM pg_indexes WHERE schemaname IN ({Schemas})
            UNION ALL
            SELECT 'pol ' || schemaname || '.' || tablename || ' ' || policyname || ' ' || coalesce(qual, '') || ' ' || coalesce(with_check, '')
              FROM pg_policies WHERE schemaname IN ({Schemas})
            UNION ALL
            SELECT 'fn ' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ') '
                   || md5(pg_get_functiondef(p.oid)) || ' ' || pg_get_userbyid(p.proowner) || ' ' || coalesce(p.proacl::text, '')
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN ({Schemas})
            UNION ALL
            SELECT 'trg ' || t.tgrelid::regclass || ' ' || t.tgname || ' ' || pg_get_triggerdef(t.oid)
              FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE NOT t.tgisinternal AND n.nspname IN ({Schemas})
            UNION ALL
            SELECT 'acl ' || n.nspname || '.' || c.relname || ' ' || coalesce(c.relacl::text, '') || ' rls=' || c.relrowsecurity || c.relforcerowsecurity
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname IN ({Schemas}) AND c.relkind IN ('r','p')
            UNION ALL
            SELECT 'nsp ' || nspname || ' ' || coalesce(nspacl::text, '') FROM pg_namespace WHERE nspname IN ({Schemas})
            """;
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "schema"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (database/schema).");
    }
}
