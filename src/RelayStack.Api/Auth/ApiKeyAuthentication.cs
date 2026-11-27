using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string ScopeClaim = "scope";
    public const string KeyIdClaim = "key_id";
    public const string OrganizationClaim = "org_id";

    public static string Policy(string scope) => "scope:" + scope;
}

/// <summary>
/// Authenticates <c>Authorization: Bearer rs_dev_...</c>. The key's public prefix is looked up through a
/// SECURITY DEFINER function (the API role cannot read api_keys), the secret is verified against its keyed
/// hash in constant time, and the tenant context is established for the rest of the request.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource,
    ApiKeyHasher hasher,
    TenantContext tenant,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Unsupported authorization scheme.");
        }

        var parsed = ApiKeyHasher.Parse(header["Bearer ".Length..].Trim());
        if (parsed is null)
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        await using var cmd = dataSource.CreateCommand("SELECT * FROM auth_lookup_api_key($1)");
        cmd.Parameters.AddWithValue(parsed.Prefix);
        await using var r = await cmd.ExecuteReaderAsync(Context.RequestAborted);
        if (!await r.ReadAsync(Context.RequestAborted))
        {
            // Hash anyway so unknown and known prefixes take similar time.
            hasher.Verify(parsed.Secret, new byte[32]);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var keyId = r.GetGuid(r.GetOrdinal("id"));
        var orgId = r.GetGuid(r.GetOrdinal("organization_id"));
        var projectId = r.GetGuid(r.GetOrdinal("project_id"));
        var envId = r.GetGuid(r.GetOrdinal("environment_id"));
        var secretHash = r.GetFieldValue<byte[]>(r.GetOrdinal("secret_hash"));
        var scopes = r.GetFieldValue<string[]>(r.GetOrdinal("scopes"));
        var expiresAt = r.IsDBNull(r.GetOrdinal("expires_at")) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("expires_at"));
        var revokedAt = r.IsDBNull(r.GetOrdinal("revoked_at")) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("revoked_at"));
        var envKind = r.GetString(r.GetOrdinal("environment_kind"));

        if (!hasher.Verify(parsed.Secret, secretHash))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var now = clock.GetUtcNow();
        if (revokedAt is not null || (expiresAt is { } exp && exp <= now) || ApiKeyHasher.MarkerFor(envKind) != parsed.EnvironmentMarker)
        {
            return AuthenticateResult.Fail("API key is revoked, expired or for a different environment.");
        }

        tenant.Set(new TenantScope(orgId, projectId, envId, envKind, keyId));

        var claims = new List<Claim>
        {
            new(ApiKeyDefaults.KeyIdClaim, keyId.ToString()),
            new(ApiKeyDefaults.OrganizationClaim, orgId.ToString()),
        };
        claims.AddRange(scopes.Select(s => new Claim(ApiKeyDefaults.ScopeClaim, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyDefaults.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Results.Problem(statusCode: 401, title: "Unauthorized", detail: "A valid API key is required.",
            extensions: new Dictionary<string, object?> { ["code"] = "unauthorized" }).ExecuteAsync(Context);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Results.Problem(statusCode: 403, title: "Forbidden", detail: "The API key does not have the required scope.",
            extensions: new Dictionary<string, object?> { ["code"] = "insufficient_scope" }).ExecuteAsync(Context);
    }
}
