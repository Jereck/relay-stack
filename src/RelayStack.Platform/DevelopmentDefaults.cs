namespace RelayStack.Platform;

/// <summary>
/// Secrets committed to the repository for local development ONLY (appsettings.Development.json, compose).
/// Hosts refuse to start outside Development if any of these values is configured.
/// </summary>
public static class DevelopmentDefaults
{
    public const string ApiKeyPepper = "w1gPP58Ge2LuuO+LkzUBOQN4m9FRqM5QcFowPMfnMXs=";
    public const string EncryptionKey = "DKJD0LSsdsIahStQhcuHmmtUAtLa0OxIsdnTCQWvgWQ=";

    public static void EnsureNotUsedOutsideDevelopment(PlatformOptions options, bool isDevelopmentHost)
    {
        if (isDevelopmentHost)
        {
            return;
        }

        if (options.Security.ApiKeyPepper == ApiKeyPepper || options.Security.EncryptionKeys.Values.Contains(EncryptionKey))
        {
            throw new InvalidOperationException("Development secrets are configured in a non-Development environment. Configure real secrets.");
        }
    }
}
