using System.Security.Cryptography;
using System.Text;

namespace Trinetra.Federation.Storage.Secrets;

/// <summary>Ciphertext plus the parameters needed to decrypt it.</summary>
public readonly record struct SealedSecret(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);

/// <summary>
/// AES-256-GCM sealing for credentials at rest.
/// </summary>
/// <remarks>
/// <para>
/// Encryption happens in the application rather than via pgcrypto so the key never travels to
/// PostgreSQL, never appears in a query, and never reaches the statement log. A DBA with full
/// table access still cannot read a camera password.
/// </para>
/// <para>
/// GCM is authenticated encryption: a tampered ciphertext fails to decrypt rather than
/// returning plausible garbage that would then be sent to a device as a password.
/// </para>
/// </remarks>
public sealed class SecretEncryption
{
    private const int NonceBytes = 12;  // GCM standard
    private const int TagBytes = 16;
    private const int KeyBytes = 32;    // AES-256

    private readonly byte[] _key;

    public SecretEncryption(string keyId, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (key.Length != KeyBytes)
        {
            throw new ArgumentException(
                $"Encryption key must be exactly {KeyBytes} bytes (AES-256); got {key.Length}.",
                nameof(key));
        }

        KeyId = keyId;
        _key = key.ToArray();
    }

    /// <summary>
    /// Identifier of the key in use, stored alongside each row.
    /// </summary>
    /// <remarks>
    /// Recording which key sealed each secret is what makes rotation possible without a flag
    /// day: new writes use the new key while existing rows stay readable under the old one.
    /// </remarks>
    public string KeyId { get; }

    /// <summary>Builds an instance from a base64 key, as supplied by config or a KMS.</summary>
    public static SecretEncryption FromBase64Key(string keyId, string base64Key)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "The credential encryption key is not valid base64. Generate one with: "
                + "openssl rand -base64 32", ex);
        }

        return new SecretEncryption(keyId, key);
    }

    /// <summary>Generates a fresh 256-bit key, base64 encoded. For provisioning only.</summary>
    public static string GenerateKeyBase64() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public SealedSecret Seal(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        // The plaintext copy is cleared rather than left for the GC: a credential sitting in a
        // freed buffer can survive into a crash dump or a swapped page.
        CryptographicOperations.ZeroMemory(plainBytes);

        return new SealedSecret(ciphertext, nonce, tag, KeyId);
    }

    /// <summary>
    /// Opens a sealed secret.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// Thrown when the key is wrong or the ciphertext was tampered with. Deliberately not
    /// caught and softened here — a decryption failure must never degrade into an empty
    /// password that then gets sent to a device.
    /// </exception>
    public string Open(SealedSecret sealedSecret)
    {
        var plainBytes = new byte[sealedSecret.Ciphertext.Length];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, plainBytes);

        var result = Encoding.UTF8.GetString(plainBytes);
        CryptographicOperations.ZeroMemory(plainBytes);

        return result;
    }
}
