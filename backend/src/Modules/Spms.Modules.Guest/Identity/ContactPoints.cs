using System.Text.RegularExpressions;
using Spms.SharedKernel;

namespace Spms.Modules.Guest.Identity;

/// <summary>
/// Contact values are restricted data: stored encrypted (contact_cipher), found
/// by a keyed hash of the normalised value (lookup_hash), and shown only as a
/// masked hint. One normalisation, used for storage and search alike, or a
/// guest typing "Ava@Example.com " would never match their own record.
/// </summary>
public static partial class ContactValues
{
    public const string CipherPurpose = "guest.contact_point.value";

    public static string LookupPurpose(string contactType) => "guest.contact_point.lookup." + contactType;

    public static string? Normalise(string contactType, string raw)
    {
        var v = raw.Trim();
        switch (contactType)
        {
            case "Email":
                v = v.ToLowerInvariant();
                return EmailShape().IsMatch(v) && v.Length <= 254 ? v : null;
            case "Mobile":
            case "Phone":
                var digits = new string(v.Where(char.IsDigit).ToArray());
                if (digits.Length is < 7 or > 15) return null;
                return (v.StartsWith('+') ? "+" : "") + digits;
            case "Address":
                return v.Length is > 0 and <= 500 ? Regex.Replace(v, @"\s+", " ") : null;
            default:
                return null;
        }
    }

    /// <summary>What a screen may show: never the whole value.</summary>
    public static string Mask(string contactType, string normalised)
    {
        switch (contactType)
        {
            case "Email":
                var at = normalised.IndexOf('@');
                return at <= 1 ? "***" + normalised[at..] : normalised[0] + "***" + normalised[at..];
            case "Mobile":
            case "Phone":
                return "***" + normalised[^Math.Min(4, normalised.Length)..];
            default:
                return normalised.Length <= 6 ? "***" : normalised[..3] + "***";
        }
    }

    public static (ProtectedValue Cipher, string LookupHash, string Hint) Protect(IFieldProtector protector, string contactType, string normalised) =>
        (protector.ProtectString(normalised, CipherPurpose), protector.LookupHash(normalised, LookupPurpose(contactType)), Mask(contactType, normalised));

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();
}
