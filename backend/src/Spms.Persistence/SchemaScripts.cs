using System.Reflection;

namespace Spms.Persistence;

/// <summary>
/// SQL embedded into this assembly.
///
/// Each migration's SQL is frozen beside it under Migrations/Sql/&lt;name&gt;/:
/// once a migration has run anywhere, its body must never change, so it
/// cannot read the living reference schema (database/schema), which moves on
/// with every model change. The two are held together by a test that builds a
/// database each way and requires them to match object for object.
///
/// 000_roles.sql is cluster-level and idempotent; the bootstrapper applies it
/// with an administrative connection before any migration.
/// </summary>
public static class SchemaScripts
{
    private static readonly Assembly Asm = typeof(SchemaScripts).Assembly;

    public static string Roles() => Read("Spms.Schema.000_roles.sql");

    /// <summary>The SQL files of one migration folder, in file-name order.</summary>
    public static IReadOnlyList<(string Name, string Sql)> ForMigration(string folder)
    {
        // RecursiveDir arrives with the platform separator baked in at build time.
        var prefixes = new[] { $"Spms.Migrations.{folder}\\", $"Spms.Migrations.{folder}/" };
        return Asm.GetManifestResourceNames()
            .Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Select(n => (Name: n[(n.LastIndexOfAny(['\\', '/']) + 1)..], Sql: Read(n)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Read(string name)
    {
        using var s = Asm.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Embedded SQL {name} is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
