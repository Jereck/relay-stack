using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayStack.Platform.Security;

/// <summary>
/// Webhook signatures following the Standard Webhooks specification (https://www.standardwebhooks.com/):
/// <c>webhook-signature: v1,base64(HMAC-SHA256(secret, "{webhook-id}.{webhook-timestamp}.{body}"))</c>.
/// The webhook-id is the stable event id, identical across retries and replays, so receivers can dedupe.
/// </summary>
public static class WebhookSigner
{
    public const string SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static string GenerateSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string messageId, long timestampSeconds, string body)
    {
        var key = DecodeSecret(secret);
        var payload = Encoding.UTF8.GetBytes($"{messageId}.{timestampSeconds.ToString(CultureInfo.InvariantCulture)}.{body}");
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }

    /// <summary>Receiver-side verification, used by the dev receiver, samples and tests.</summary>
    public static bool Verify(string secret, string messageId, string timestampHeader, string signatureHeader, string body,
        DateTimeOffset now, TimeSpan tolerance)
    {
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
        {
            return false;
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(ts);
        if (sent < now - tolerance || sent > now + tolerance)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, messageId, ts, body));
        // The header may carry several space-separated signatures (secret rotation).
        foreach (var candidate in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] DecodeSecret(string secret)
    {
        var raw = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        return Convert.FromBase64String(raw);
    }
}
