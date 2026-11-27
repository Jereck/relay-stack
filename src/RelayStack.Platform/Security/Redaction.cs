using System.Text.RegularExpressions;

namespace RelayStack.Platform.Security;

/// <summary>Removes secrets from values that may reach logs, error messages or API responses.</summary>
public static partial class Redaction
{
    /// <summary>Keeps scheme, host, port and path. Drops user info, query string and fragment, which often carry tokens.</summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "[invalid-url]";
        }

        var hasExtra = uri.Query.Length > 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}" + (hasExtra ? "?[redacted]" : "");
    }

    /// <summary>Masks anything that looks like a RelayStack API key or webhook secret, then truncates.</summary>
    public static string Text(string? value, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var masked = SecretPattern().Replace(value, m => m.Value[..Math.Min(8, m.Value.Length)] + "[redacted]");
        return masked.Length <= maxLength ? masked : masked[..maxLength] + "…";
    }

    [GeneratedRegex(@"(rs_(dev|prod)_[a-z0-9]{12}_[A-Za-z0-9]{43}|whsec_[A-Za-z0-9+/=]{16,}|Bearer\s+[A-Za-z0-9._\-+/=]{8,})", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
