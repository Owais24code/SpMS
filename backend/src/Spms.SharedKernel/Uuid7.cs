using System.Security.Cryptography;

namespace Spms.SharedKernel;

/// <summary>
/// UUIDv7 (RFC 9562): a 48-bit Unix millisecond timestamp, version 7, variant
/// 10, then random bits. The same layout as core.uuid_v7() in SQL.
///
/// The application mints ids itself because a command knows its id before it
/// commits: the audit row, the outbox event and the Location header all quote
/// it, and none of them can wait for a RETURNING clause. Time-ordered ids also
/// keep B-tree inserts at the right-hand edge of the index.
///
/// Within one millisecond ids are not guaranteed to sort in creation order;
/// nothing relies on that.
/// </summary>
public static class Uuid7
{
    public static Guid New() => At(DateTimeOffset.UtcNow);

    public static Guid At(DateTimeOffset instant)
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);

        var ms = instant.ToUnixTimeMilliseconds();
        b[0] = (byte)(ms >> 40);
        b[1] = (byte)(ms >> 32);
        b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16);
        b[4] = (byte)(ms >> 8);
        b[5] = (byte)ms;
        b[6] = (byte)((b[6] & 0x0F) | 0x70);   // version 7
        b[8] = (byte)((b[8] & 0x3F) | 0x80);   // variant 10

        // Big-endian: the byte order above is the textual order of the UUID.
        return new Guid(b, bigEndian: true);
    }

    /// <summary>The millisecond timestamp an id was minted at.</summary>
    public static DateTimeOffset TimestampOf(Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        long ms = ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24)
                | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    public static bool IsVersion7(Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        return (b[6] >> 4) == 7 && (b[8] >> 6) == 2;
    }
}
