using System.Security.Cryptography;
using System.Text;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Platform.Security;

public static class Scopes
{
    public const string InboxesRead = "inboxes:read";
    public const string InboxesWrite = "inboxes:write";
    public const string FilesRead = "files:read";
    public const string FilesWrite = "files:write";
    public const string EventsRead = "events:read";
    public const string DeliveriesRead = "deliveries:read";
    public const string DeliveriesReplay = "deliveries:replay";
    public const string DestinationsRead = "destinations:read";
    public const string DestinationsWrite = "destinations:write";
    public const string UsageRead = "usage:read";
    public const string PollingWrite = "polling:write";
    public const string PollingRead = "polling:read";

    public static readonly IReadOnlyList<string> All =
    [
        InboxesRead, InboxesWrite, FilesRead, FilesWrite, EventsRead, DeliveriesRead, DeliveriesReplay,
        DestinationsRead, DestinationsWrite, UsageRead, PollingRead, PollingWrite,
    ];

    public static bool IsValid(string scope) => All.Contains(scope);
}

public sealed record GeneratedApiKey(string FullKey, string Prefix, byte[] SecretHash);

public sealed record ParsedApiKey(string EnvironmentMarker, string Prefix, string Secret);

/// <summary>
/// API key format: <c>rs_{dev|prod}_{id}_{secret}</c>.
/// <list type="bullet">
/// <item>id: 12 lowercase alphanumerics, public, used to look the key up.</item>
/// <item>secret: 43 base62 characters (~256 bits), never stored. Only HMAC-SHA256(pepper, secret) is stored.</item>
/// </list>
/// A fast keyed hash is appropriate because the secret is high-entropy random data, not a human password.
/// </summary>
public sealed class ApiKeyHasher
{
    private const string LowerAlnum = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string Base62 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly byte[] _pepper;

    public ApiKeyHasher(string base64Pepper)
    {
        _pepper = string.IsNullOrWhiteSpace(base64Pepper) ? [] : Convert.FromBase64String(base64Pepper);
        if (_pepper.Length < 32)
        {
            throw new InvalidOperationException("RelayStack:Security:ApiKeyPepper must be a base64 value of at least 32 bytes.");
        }
    }

    public static string MarkerFor(string environmentKind) => environmentKind switch
    {
        EnvironmentKinds.Development => "dev",
        EnvironmentKinds.Production => "prod",
        _ => throw new ArgumentOutOfRangeException(nameof(environmentKind)),
    };

    public GeneratedApiKey Generate(string environmentKind)
    {
        var prefix = RandomNumberGenerator.GetString(LowerAlnum, 12);
        var secret = RandomNumberGenerator.GetString(Base62, 43);
        return new GeneratedApiKey($"rs_{MarkerFor(environmentKind)}_{prefix}_{secret}", prefix, Hash(secret));
    }

    public byte[] Hash(string secret) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(secret));

    public bool Verify(string secret, byte[] storedHash) => CryptographicOperations.FixedTimeEquals(Hash(secret), storedHash);

    public static ParsedApiKey? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return null;
        }

        var parts = value.Split('_');
        if (parts.Length != 4 || parts[0] != "rs" || parts[1] is not ("dev" or "prod"))
        {
            return null;
        }

        if (parts[2].Length != 12 || !parts[2].All(LowerAlnum.Contains) || parts[3].Length != 43 || !parts[3].All(Base62.Contains))
        {
            return null;
        }

        return new ParsedApiKey(parts[1], parts[2], parts[3]);
    }
}
