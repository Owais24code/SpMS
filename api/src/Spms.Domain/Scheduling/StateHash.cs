using System.Security.Cryptography;
using System.Text;

namespace Spms.Domain.Scheduling;

/// <summary>
/// Hashes the fields an audit reader needs to prove changed, without copying
/// them into the trail. §54.3 requires before/after hashes on critical tables.
///
/// RowVersion is deliberately NOT in the canonical string. It was, and because
/// it bumps on every write the two hashes always differed — so they could not
/// prove that anything substantive changed, which is the one thing they exist
/// to do. A move that lands an appointment back on its own start, provider and
/// room now produces identical hashes, correctly.
/// </summary>
public static class StateHash
{
    public static string Of(Appointment a)
    {
        var canonical = string.Join('|',
            a.AppointmentId, a.Status,
            a.StartUtc.ToUnixTimeSeconds(), a.DurationMinutes,
            a.ProviderId ?? "-", a.RoomId ?? "-", a.ServiceId, a.GuestId);

        // Truncated to 32 hex characters (128 bits) to keep audit rows compact.
        // Collision resistance at 128 bits is ample for tamper evidence here.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }
}
