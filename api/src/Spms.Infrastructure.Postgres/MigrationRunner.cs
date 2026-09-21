using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Spms.Infrastructure.Postgres;

/// <summary>
/// Applies the versioned SQL files in Migrations/ and records what it applied.
///
/// SQL files, not EF migrations, own the schema. The two things this slice
/// depends on most — a stored end_utc maintained by a trigger and a GiST
/// exclusion constraint — are not expressible through EF's model builder, so
/// generating the schema from the model would mean hand-editing the generated
/// migration anyway. One source of truth, readable by a DBA, is better than
/// two that can disagree.
///
/// Each file's checksum is stored, so a migration edited after it was applied
/// is detected rather than silently divergent (TST-023).
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, ILoggerLike log)
{
    public sealed record Applied(string Version, string FileName, bool AlreadyPresent);

    public async Task<IReadOnlyList<Applied>> RunAsync(CancellationToken ct = default)
    {
        var files = Discover();
        if (files.Count == 0) throw new InvalidOperationException("No migration resources were embedded.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // The ledger has to exist before anything can be recorded, and V003 is
        // what creates it — so on a virgin database the first pass runs
        // without it and records afterwards.
        var ledgerExists = await TableExistsAsync(conn, "schema_migration", ct);

        var results = new List<Applied>();
        var seen = ledgerExists ? await LoadLedgerAsync(conn, ct) : new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (version, name, sql) in files)
        {
            var checksum = Checksum(sql);

            if (seen.TryGetValue(version, out var recorded))
            {
                if (recorded != checksum)
                    throw new InvalidOperationException(
                        $"Migration {version} ({name}) was applied with checksum {recorded} but the file now hashes to " +
                        $"{checksum}. An applied migration must never be edited — add a new one instead.");

                results.Add(new Applied(version, name, AlreadyPresent: true));
                continue;
            }

            log.Info($"Applying migration {version} {name}");

            // Each file in its own transaction: a failure leaves earlier
            // migrations applied and recorded, which is what makes a re-run
            // after a fix safe.
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);

            if (await TableExistsAsync(conn, "schema_migration", ct, tx))
            {
                await using var rec = new NpgsqlCommand(
                    "INSERT INTO schema_migration (version, file_name, checksum) VALUES (@v, @f, @c) " +
                    "ON CONFLICT (version) DO UPDATE SET checksum = EXCLUDED.checksum, file_name = EXCLUDED.file_name;",
                    conn, tx);
                rec.Parameters.AddWithValue("v", version);
                rec.Parameters.AddWithValue("f", name);
                rec.Parameters.AddWithValue("c", checksum);
                await rec.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            results.Add(new Applied(version, name, AlreadyPresent: false));
        }

        // Backfill any version applied before the ledger table existed.
        foreach (var (version, name, sql) in files)
        {
            await using var rec = new NpgsqlCommand(
                "INSERT INTO schema_migration (version, file_name, checksum) VALUES (@v, @f, @c) " +
                "ON CONFLICT (version) DO NOTHING;", conn);
            rec.Parameters.AddWithValue("v", version);
            rec.Parameters.AddWithValue("f", name);
            rec.Parameters.AddWithValue("c", Checksum(sql));
            await rec.ExecuteNonQueryAsync(ct);
        }

        return results;
    }

    private static List<(string Version, string Name, string Sql)> Discover()
    {
        var asm = typeof(MigrationRunner).Assembly;
        var found = new List<(string, string, string)>();

        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();

            var name = res.Split('.').Reverse().Skip(1).First();   // V001__reference_and_identity
            var version = name.Split("__")[0];                     // V001
            found.Add((version, name, sql));
        }

        // Lexicographic ordering is the contract: V001 before V002 before V010.
        return found.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
    }

    private static async Task<Dictionary<string, string>> LoadLedgerAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migration;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection conn, string table, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=@t);",
            conn, tx);
        cmd.Parameters.AddWithValue("t", table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static string Checksum(string sql)
    {
        // Line endings are normalised first, or the same file checked out on
        // Windows and Linux would hash differently and read as tampered.
        var normalised = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..32];
    }
}

/// <summary>
/// A one-method logging seam, so this project does not take a dependency on
/// Microsoft.Extensions.Logging purely to print two lines.
/// </summary>
public interface ILoggerLike
{
    void Info(string message);
}

public sealed class ConsoleLoggerLike : ILoggerLike
{
    public void Info(string message) => Console.WriteLine(message);
}

/// <summary>
/// Checks that the database the application is talking to actually has the
/// structures the code depends on.
///
/// Mapping drift is the standard failure of a schema owned by SQL and mapped
/// by an ORM: the model compiles, the app starts, and the first query fails in
/// production. Worse, the exclusion constraint could be dropped by hand and
/// nothing would notice — the application would keep evaluating conflicts and
/// keep being wrong about the one it cannot enforce itself.
/// </summary>
public sealed class SchemaGuard(NpgsqlDataSource dataSource)
{
    private static readonly string[] RequiredTables =
    [
        "tenant", "property", "service", "room", "staff", "staff_qualification", "guest",
        "buffer_policy", "appointment", "audit_entry", "idempotency_key", "preflight_token",
        "schema_migration",
    ];

    private static readonly string[] RequiredConstraints =
    [
        // The one the application cannot enforce on its own.
        "appointment_room_no_overlap",
        "appointment_duration_positive",
        "appointment_interval_forward",
        // Without this, status is just text and a typo could hide a row from
        // the room-overlap constraint's WHERE clause.
        "appointment_status_known",
    ];

    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public';", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct)) present.Add(r.GetString(0));
            problems.AddRange(RequiredTables.Where(t => !present.Contains(t)).Select(t => $"missing table: {t}"));
        }

        await using (var cmd = new NpgsqlCommand("SELECT conname FROM pg_constraint;", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct)) present.Add(r.GetString(0));
            problems.AddRange(RequiredConstraints.Where(c => !present.Contains(c)).Select(c => $"missing constraint: {c}"));
        }

        // The trigger that maintains end_utc. Without it every inserted row
        // would fail the interval check, or worse, be indexed on a stale end.
        await using (var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='appointment_end_utc' AND NOT tgisinternal);", conn))
        {
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) problems.Add("missing trigger: appointment_end_utc");
        }

        // The rules that make the audit trail append-only. A trail the
        // application can rewrite is not evidence.
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_rules WHERE schemaname='public' AND tablename='audit_entry';", conn))
        {
            var rules = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            if (rules < 2) problems.Add($"audit_entry is not append-only: expected 2 rules, found {rules}");
        }

        return problems;
    }
}
