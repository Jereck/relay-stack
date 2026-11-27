using System.Security.Cryptography;
using System.Text;

namespace RelayStack.Platform.Security;

/// <summary>
/// Encrypts credentials at rest with AES-256-GCM.
/// Envelope: [version=1][key id length][key id][nonce 12][tag 16][ciphertext].
/// The associated data binds each ciphertext to its purpose and owning organization, so a ciphertext copied
/// into another tenant's row (or used for another purpose) fails to decrypt.
/// Production replacement: per-tenant data keys wrapped by AWS KMS (see docs/SECURITY_MODEL.md).
/// </summary>
public sealed class SecretProtector
{
    private const byte Version = 1;
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _currentKeyId;

    public SecretProtector(IReadOnlyDictionary<string, string> base64Keys, string currentKeyId)
    {
        _keys = base64Keys.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
        if (_keys.Count == 0 || _keys.Values.Any(k => k.Length != 32))
        {
            throw new InvalidOperationException("RelayStack:Security:EncryptionKeys must contain base64-encoded 32-byte keys.");
        }

        if (!_keys.ContainsKey(currentKeyId) || Encoding.UTF8.GetByteCount(currentKeyId) > 32)
        {
            throw new InvalidOperationException("RelayStack:Security:CurrentEncryptionKeyId must name a configured key.");
        }

        _currentKeyId = currentKeyId;
    }

    public byte[] Protect(string plaintext, string purpose, Guid organizationId)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(_currentKeyId);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[2 + keyIdBytes.Length + 12 + 16 + plain.Length];
        output[0] = Version;
        output[1] = (byte)keyIdBytes.Length;
        keyIdBytes.CopyTo(output, 2);
        var offset = 2 + keyIdBytes.Length;
        var nonce = output.AsSpan(offset, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[_currentKeyId], 16);
        aes.Encrypt(nonce, plain, output.AsSpan(offset + 28), output.AsSpan(offset + 12, 16), Aad(purpose, organizationId));
        return output;
    }

    public string Unprotect(byte[] envelope, string purpose, Guid organizationId)
    {
        if (envelope.Length < 2 || envelope[0] != Version)
        {
            throw new CryptographicException("Unsupported secret envelope.");
        }

        var keyIdLength = envelope[1];
        var keyId = Encoding.UTF8.GetString(envelope, 2, keyIdLength);
        if (!_keys.TryGetValue(keyId, out var key))
        {
            throw new CryptographicException("Secret was encrypted with an unknown key.");
        }

        var offset = 2 + keyIdLength;
        var cipherLength = envelope.Length - offset - 28;
        if (cipherLength < 0)
        {
            throw new CryptographicException("Secret envelope is truncated.");
        }

        var plain = new byte[cipherLength];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(envelope.AsSpan(offset, 12), envelope.AsSpan(offset + 28), envelope.AsSpan(offset + 12, 16), plain, Aad(purpose, organizationId));
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Aad(string purpose, Guid organizationId) => Encoding.UTF8.GetBytes($"relaystack:{purpose}:{organizationId:N}");
}

public static class SecretPurposes
{
    public const string WebhookSigningSecret = "webhook-signing-secret";
}
