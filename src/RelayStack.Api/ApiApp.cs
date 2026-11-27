using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using RelayStack.Api.Auth;
using RelayStack.Api.Endpoints;
using RelayStack.Platform;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;

namespace RelayStack.Api;

public static class ApiApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        var connectionString = builder.Configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required (the relaystack_app role).");

        builder.Services.AddRelayStackObservability(builder.Logging, builder.Configuration, "relaystack-api");
        builder.Services.AddRelayStackPlatform(options, DatabaseAccessMode.Tenant, connectionString, builder.Environment.IsDevelopment());

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.Limits.MaxJsonRequestBytes; // raised per request for uploads only
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "RelayStack API";
            doc.Info.Version = "v1";
            doc.Info.Description = "Phase 0 proof of concept. Authenticate with `Authorization: Bearer rs_dev_...`.";
            return Task.CompletedTask;
        }));

        builder.Services.AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
        // Every endpoint requires an authenticated API key unless it explicitly allows anonymous access.
        var authorization = builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(ApiKeyDefaults.Scheme).RequireAuthenticatedUser().Build());
        foreach (var scope in Scopes.All)
        {
            authorization.AddPolicy(ApiKeyDefaults.Policy(scope), p => p.AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                .RequireAuthenticatedUser().RequireClaim(ApiKeyDefaults.ScopeClaim, scope));
        }

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ApiErrors.Problem(429, "rate_limited", "Too many requests.").ExecuteAsync(ctx.HttpContext);
            };
            // Runs after authentication: authenticated traffic is partitioned per API key, anonymous traffic per client IP.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var keyId = ctx.User.FindFirst(ApiKeyDefaults.KeyIdClaim)?.Value;
                return keyId is not null
                    ? RateLimitPartition.GetTokenBucketLimiter("key:" + keyId, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.Limits.ApiBurstPerKey,
                        TokensPerPeriod = options.Limits.ApiRequestsPerSecondPerKey,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                    });
            });
            o.AddPolicy(FileEndpoints.UploadRateLimitPolicy, ctx => RateLimitPartition.GetTokenBucketLimiter(
                "upload:" + (ctx.User.FindFirst(ApiKeyDefaults.KeyIdClaim)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString()),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.Limits.UploadsPerMinutePerKey / 6),
                    TokensPerPeriod = options.Limits.UploadsPerMinutePerKey,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            p.WithOrigins(origins).WithHeaders("Authorization", "Content-Type", "Idempotency-Key").WithMethods("GET", "POST");
        }));

        builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.None);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();
        if (app.Environment.IsDevelopment())
        {
            // Never exposed outside Development until the API surface has been security reviewed.
            app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        }

        var v1 = app.MapGroup("/v1");
        v1.MapConfigurationEndpoints();
        v1.MapFileEndpoints();
        v1.MapDeliveryEndpoints();
        v1.MapPollingEndpoints();
        return app;
    }
}
