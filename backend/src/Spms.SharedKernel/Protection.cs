using System.Security.Cryptography;
using System.Text;

namespace Spms.SharedKernel;

/// <summary>
/// Application-level envelope encryption for restricted columns
/// (<c>*_cipher bytea</c> + <c>key_version</c>): intake answers, treatment
/// notes, contact values, credential numbers, consent evidence, stored
/// idempotent responses.
///
/// The database and its backups hold ciphertext only. A data key (DEK) per
/// key version encrypts with AES-256-GCM; in a deployed environment the DEKs
/// are themselves wrapped by a Key Vault key (<see cref="IKeyRing"/>), so
/// rotating the vault key never re-encrypts rows and revoking it makes every
/// row unreadable at once.
///
/// The purpose is bound into the authenticated data, so a cipher lifted from
/// one column and written into another fails to decrypt instead of quietly
/// revealing, say, a treatment note through the contact-point path.
/// </summary>
public interface IFieldProtector
{
    ProtectedValue Protect(ReadOnlySpan<byte> plaintext, string purpose);
    byte[] Unprotect(byte[] cipher, string keyVersion, string purpose);

    /// <summary>
    /// A keyed hash of a normalised value, for equality search without
    /// decryption (<c>lookup_hash</c>). HMAC rather than a bare hash, because a
    /// bare SHA-256 of a phone number is reversible by enumeration.
    /// </summary>
    string LookupHash(string normalizedValue, string purpose);
}

public readonly record struct ProtectedValue(byte[] Cipher, string KeyVersion);

public static class FieldProtectorExtensions
{
    public static ProtectedValue ProtectString(this IFieldProtector p, string value, string purpose) =>
        p.Protect(Encoding.UTF8.GetBytes(value), purpose);

    public static string UnprotectString(this IFieldProtector p, byte[] cipher, string keyVersion, string purpose) =>
        Encoding.UTF8.GetString(p.Unprotect(cipher, keyVersion, purpose));
}

/// <summary>Where data keys come from.</summary>
public interface IKeyRing
{
    string ActiveVersion { get; }
    ReadOnlySpan<byte> DataKey(string version);
    ReadOnlySpan<byte> LookupKey { get; }
}

/// <summary>
/// AES-256-GCM over a key ring. Layout: nonce (12) | tag (16) | ciphertext.
/// </summary>
public sealed class AesGcmFieldProtector(IKeyRing keys) : IFieldProtector
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public ProtectedValue Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var version = keys.ActiveVersion;
        var output = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(keys.DataKey(version), TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceBytes + TagBytes), output.AsSpan(NonceBytes, TagBytes),
                    Aad(purpose, version));
        return new ProtectedValue(output, version);
    }

    public byte[] Unprotect(byte[] cipher, string keyVersion, string purpose)
    {
        if (cipher.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Cipher is too short to be a protected value.");

        var plaintext = new byte[cipher.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(keys.DataKey(keyVersion), TagBytes);
        aes.Decrypt(cipher.AsSpan(0, NonceBytes), cipher.AsSpan(NonceBytes + TagBytes),
                    cipher.AsSpan(NonceBytes, TagBytes), plaintext, Aad(purpose, keyVersion));
        return plaintext;
    }

    public string LookupHash(string normalizedValue, string purpose)
    {
        var mac = HMACSHA256.HashData(keys.LookupKey, Encoding.UTF8.GetBytes(purpose + "\n" + normalizedValue));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static byte[] Aad(string purpose, string version) => Encoding.UTF8.GetBytes($"spms/{purpose}/{version}");
}

/// <summary>
/// Data keys supplied directly (base64, 32 bytes each), for development and
/// tests. Deployed environments use the Key Vault-wrapped ring instead; this
/// type refuses to start without keys rather than inventing one, so a
/// misconfigured deployment cannot write ciphertext nobody can read later.
/// </summary>
public sealed class StaticKeyRing : IKeyRing
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly byte[] _lookup;

    public StaticKeyRing(IReadOnlyDictionary<string, string> base64Keys, string activeVersion, string lookupKeyBase64)
    {
        _keys = base64Keys.ToDictionary(k => k.Key, k => Decode(k.Value, k.Key), StringComparer.Ordinal);
        if (!_keys.ContainsKey(activeVersion))
            throw new InvalidOperationException($"Active key version '{activeVersion}' is not in the key ring.");
        ActiveVersion = activeVersion;
        _lookup = Decode(lookupKeyBase64, "lookup");
    }

    public string ActiveVersion { get; }

    public ReadOnlySpan<byte> DataKey(string version) =>
        _keys.TryGetValue(version, out var k)
            ? k
            : throw new CryptographicException($"Key version '{version}' is not available.");

    public ReadOnlySpan<byte> LookupKey => _lookup;

    private static byte[] Decode(string b64, string name)
    {
        var bytes = Convert.FromBase64String(b64);
        if (bytes.Length != 32) throw new InvalidOperationException($"Key '{name}' must be 32 bytes.");
        return bytes;
    }

    /// <summary>A throwaway ring for tests.</summary>
    public static StaticKeyRing Ephemeral()
    {
        var k = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new StaticKeyRing(new Dictionary<string, string> { ["k1"] = k }, "k1",
                                 Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }
}
