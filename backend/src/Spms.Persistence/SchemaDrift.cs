using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Spms.Persistence;

/// <summary>
/// Compares the EF model with the live database, column by column.
///
/// The EF model is generated from the same source as the SQL, so drift should
/// be impossible; this is the check that proves it rather than assuming it,
/// and it also catches the opposite failure — a hand-applied hotfix that added
/// a column nothing maps. Run at start-up (the host refuses to serve on drift)
/// and in CI against a freshly migrated database.
/// </summary>
public static class SchemaDrift
{
    public static async Task<IReadOnlyList<string>> CompareAsync(DbContext db, NpgsqlConnection conn, CancellationToken ct = default)
    {
        var model = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .ToList();
        var schemas = model.Select(e => e.GetSchema()!).Distinct().ToArray();

        var live = new Dictionary<(string Schema, string Table), Dictionary<string, (string Type, bool NotNull)>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
              FROM pg_attribute a
              JOIN pg_class c ON c.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
               AND a.attnum > 0 AND NOT a.attisdropped
               AND n.nspname = ANY(@schemas)
            """, conn))
        {
            cmd.Parameters.AddWithValue("schemas", schemas);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!live.TryGetValue(key, out var cols)) live[key] = cols = new(StringComparer.Ordinal);
                cols[r.GetString(2)] = (r.GetString(3), r.GetBoolean(4));
            }
        }

        var problems = new List<string>();
        var mapped = new HashSet<(string, string)>();

        foreach (var et in model)
        {
            var key = (et.GetSchema()!, et.GetTableName()!);
            mapped.Add(key);
            if (!live.TryGetValue(key, out var cols))
            {
                problems.Add($"{key.Item1}.{key.Item2}: mapped by EF but missing from the database");
                continue;
            }

            var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(key.Item2, key.Item1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in et.GetProperties())
            {
                var name = p.GetColumnName(table);
                if (name is null) continue;
                seen.Add(name);
                if (!cols.TryGetValue(name, out var c))
                {
                    problems.Add($"{key.Item1}.{key.Item2}.{name}: mapped by EF but missing from the database");
                    continue;
                }
                var type = p.GetColumnType();
                if (!string.Equals(Normalise(type), Normalise(c.Type), StringComparison.Ordinal))
                    problems.Add($"{key.Item1}.{key.Item2}.{name}: EF type {type}, database type {c.Type}");
                if (p.IsNullable == c.NotNull)
                    problems.Add($"{key.Item1}.{key.Item2}.{name}: EF nullable={p.IsNullable}, database NOT NULL={c.NotNull}");
            }
            foreach (var extra in cols.Keys.Where(k => !seen.Contains(k)))
                problems.Add($"{key.Item1}.{key.Item2}.{extra}: in the database but not mapped by EF");
        }

        foreach (var key in live.Keys.Where(k => !mapped.Contains(k)))
            problems.Add($"{key.Schema}.{key.Table}: in the database but not mapped by EF");

        return problems;
    }

    private static string Normalise(string t) => t.Trim().ToLowerInvariant();
}
