using System.Security.Cryptography;
using System.Text;

namespace Spms.Host.Identity;

/// <summary>
/// Password storage for local accounts: PBKDF2-HMAC-SHA256 with a random
/// 16-byte salt and the iteration count stored in the value, so the cost can be
/// raised later without invalidating existing hashes.
///
///   pbkdf2-sha256$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;hash base64&gt;
/// </summary>
public static class Passwords
{
    public const int Iterations = 600_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    public const int MinLength = 10;
    public const int MaxLength = 128;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (stored is null) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 10_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spend the same time as a real check, so an unknown email is not faster to refuse than a wrong password.</summary>
    public static void Waste(string password) => Verify(password, DummyHash.Value);

    private static readonly Lazy<string> DummyHash = new(() => Hash(Guid.NewGuid().ToString()));

    /// <summary>Why a password is not acceptable, or null when it is.</summary>
    public static string? Problem(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"A password is at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"A password is at most {MaxLength} characters.";
        if (password.Distinct().Count() < 4) return "Use a password with more variety.";
        if (email is not null && email.Trim().Split('@')[0] is { Length: >= 4 } name && password.Contains(name, StringComparison.OrdinalIgnoreCase))
            return "The password must not contain your email name.";
        return null;
    }

    /// <summary>A temporary password an administrator hands over: 16 characters, no look-alikes (0/O, 1/l/I).</summary>
    public static string Temporary()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(16, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    /// <summary>The login subject of a local account: the email, trimmed and lower-cased.</summary>
    public static string Subject(string email) => email.Trim().ToLowerInvariant();

    public static bool LooksLikeEmail(string? email)
    {
        var e = email?.Trim();
        if (e is not { Length: >= 3 and <= 254 } || e.Contains(' ')) return false;
        var at = e.IndexOf('@');
        return at > 0 && at < e.Length - 1 && at == e.LastIndexOf('@');
    }
}
