using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Storage;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Platform;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers persistence, security primitives and storage for a host running in the given access mode.</summary>
    public static IServiceCollection AddRelayStackPlatform(this IServiceCollection services, PlatformOptions options,
        DatabaseAccessMode mode, string connectionString, bool isDevelopmentHost)
    {
        DevelopmentDefaults.EnsureNotUsedOutsideDevelopment(options, isDevelopmentHost);
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();

        // Factory registration so the container owns (and disposes) the data source and its pooled connections.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton<TenantConnectionInterceptor>();
        services.AddSingleton(sp => new DbContextOptionsBuilder<RelayStackDbContext>()
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>())
            .Options);
        // The access mode is fixed per host: the API is always Tenant, the worker is always Worker.
        services.AddScoped(sp => new RelayStackDbContext(
            sp.GetRequiredService<DbContextOptions<RelayStackDbContext>>(), sp.GetRequiredService<TenantContext>(), mode));

        services.AddSingleton(sp => new ApiKeyHasher(sp.GetRequiredService<PlatformOptions>().Security.ApiKeyPepper));
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<PlatformOptions>().Security;
            return new SecretProtector(s.EncryptionKeys, s.CurrentEncryptionKeyId);
        });
        services.AddSingleton(sp => new OutboundPolicy(sp.GetRequiredService<PlatformOptions>().Delivery.DevelopmentReceiverAllowlist, isDevelopmentHost));
        var storageRoot = options.Storage.RootPath;
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = isDevelopmentHost
                ? Path.Combine(Path.GetTempPath(), "relaystack", "objects")
                : throw new InvalidOperationException("RelayStack:Storage:RootPath must be configured outside Development.");
        }
        else if (!Path.IsPathFullyQualified(storageRoot))
        {
            // Relative paths silently diverge between processes started from different directories.
            throw new InvalidOperationException("RelayStack:Storage:RootPath must be an absolute path.");
        }

        services.AddSingleton<IObjectStore>(_ => new FileSystemObjectStore(storageRoot));
        return services;
    }

    /// <summary>Creates a standalone DbContext (CLI, tests, background loops that manage their own scope).</summary>
    public static RelayStackDbContext CreateContext(NpgsqlDataSource dataSource, DatabaseAccessMode mode, TenantContext? tenant = null)
    {
        var options = new DbContextOptionsBuilder<RelayStackDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        return new RelayStackDbContext(options, tenant ?? new TenantContext(), mode);
    }
}
