using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Spms.Tests.Support;

/// <summary>
/// Readable test names ("room-1", "prov-lena") to stable UUIDs and back.
///
/// The fast suite speaks in names; the database speaks in UUIDs. Rather than
/// rewrite every case in terms of opaque ids, the Postgres world translates at
/// the port boundary, so one assertion ("the room is room-1") holds against
/// both stores. The mapping is deterministic (SHA-256 of the name), so a
/// name means the same row in every test and every run.
/// </summary>
public static class TestIds
{
    private static readonly ConcurrentDictionary<Guid, string> Names = new();

    public static string Of(string name) => Guid.TryParse(name, out _) ? name : IdOf(name).ToString();

    public static string? OfNullable(string? name) => name is null ? null : Of(name);

    public static Guid IdOf(string name)
    {
        if (Guid.TryParse(name, out var g)) return g;
        var b = SHA256.HashData(Encoding.UTF8.GetBytes("spms-test:" + name)).AsSpan(0, 16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x70);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        var id = new Guid(b, bigEndian: true);
        Names[id] = name;
        return id;
    }

    /// <summary>The name an id was minted from, or the id itself for one never named (a generated id).</summary>
    public static string NameOf(string id) =>
        Guid.TryParse(id, out var g) && Names.TryGetValue(g, out var n) ? n : id;

    public static string? NameOfNullable(string? id) => id is null ? null : NameOf(id);
}
