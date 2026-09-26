using System.Reflection;

namespace Spms.Persistence;

/// <summary>
/// The reviewed schema (database/schema/*.sql, generated from database/model/),
/// embedded into this assembly. The R1 baseline migration is these files, in
/// order; 000_roles.sql is cluster-level and is applied by the bootstrapper
/// with an administrative connection instead.
/// </summary>
public static class SchemaScripts
{
    private const string Prefix = "Spms.Schema.";

    public static IReadOnlyList<(string Name, string Sql)> All()
    {
        var asm = typeof(SchemaScripts).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n[Prefix.Length..], Sql: Read(asm, n)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string Roles() => All().Single(s => s.Name == "000_roles.sql").Sql;

    /// <summary>Every module file and the security tail: the body of the baseline.</summary>
    public static IReadOnlyList<(string Name, string Sql)> Baseline() =>
        All().Where(s => s.Name != "000_roles.sql").ToList();

    private static string Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
