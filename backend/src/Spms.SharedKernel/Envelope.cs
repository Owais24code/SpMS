using System.Security.Cryptography;
using System.Text;

namespace Spms.SharedKernel;

/// <summary>
/// Envelope encryption for restricted content (SEC-008: intake answers,
/// treatment notes). Each record gets its own random data key (DEK); the
/// content is sealed with the DEK, and the DEK is sealed with the key ring's
/// key-encryption key through <see cref="IFieldProtector"/>, bound to the
/// record's purpose and id. The database and its backups hold ciphertext only,
/// and re-keying the ring re-wraps 32-byte keys, not every answer.
///
/// Wire format (one blob): 'E1' | u16 wrappedKeyLength | wrappedKey | nonce(12) | tag(16) | ciphertext.
/// The key version stored beside the blob is the version that wrapped the DEK.
/// </summary>
public sealed class EnvelopeCipher(IFieldProtector protector)
{
    private static readonly byte[] Magic = "E1"u8.ToArray();

    public ProtectedValue Seal(string plaintext, string purpose, Guid recordId)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrapped = protector.Protect(dek, Aad(purpose, recordId));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var data = Encoding.UTF8.GetBytes(plaintext);
            var ct = new byte[data.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(dek, 16))
                gcm.Encrypt(nonce, data, ct, tag, Encoding.UTF8.GetBytes(purpose));

            var blob = new byte[2 + 2 + wrapped.Cipher.Length + 12 + 16 + ct.Length];
            var span = blob.AsSpan();
            Magic.CopyTo(span);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)wrapped.Cipher.Length);
            wrapped.Cipher.CopyTo(span[4..]);
            var at = 4 + wrapped.Cipher.Length;
            nonce.CopyTo(span[at..]);
            tag.CopyTo(span[(at + 12)..]);
            ct.CopyTo(span[(at + 28)..]);
            return new ProtectedValue(blob, wrapped.KeyVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public string Open(byte[] blob, string keyVersion, string purpose, Guid recordId)
    {
        if (blob.Length < 4 || blob[0] != Magic[0] || blob[1] != Magic[1])
            throw new CryptographicException("Not an envelope.");
        var span = blob.AsSpan();
        var wrappedLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        var at = 4 + wrappedLength;
        if (blob.Length < at + 28) throw new CryptographicException("Truncated envelope.");

        var dek = protector.Unprotect(span.Slice(4, wrappedLength).ToArray(), keyVersion, Aad(purpose, recordId));
        try
        {
            var nonce = span.Slice(at, 12);
            var tag = span.Slice(at + 12, 16);
            var ct = span[(at + 28)..];
            var data = new byte[ct.Length];
            using (var gcm = new AesGcm(dek, 16))
                gcm.Decrypt(nonce, ct, tag, data, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>The DEK is bound to the record: a blob copied onto another row does not open.</summary>
    private static string Aad(string purpose, Guid recordId) => $"envelope:{purpose}:{recordId:N}";
}
