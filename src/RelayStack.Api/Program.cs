using Npgsql;
using RelayStack.Api;
using RelayStack.Platform;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

// Operator commands run against the schema-owner connection and exit. Everything else starts the API.
//   migrate
//   bootstrap --org "Acme Dev" --project "Payroll sync" [--scopes a,b] [--key-name local]
//   create-key --org <id> --project <id> --environment development|production --scopes a,b [--name ci]
if (args.Length > 0 && args[0] is "migrate" or "bootstrap" or "create-key")
{
    return await OperatorCli.RunAsync(args);
}

var app = ApiApp.Build(args);
await app.RunAsync();
return 0;

public partial class Program;

internal static class OperatorCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var owner = config.GetConnectionString("Owner") ?? throw new InvalidOperationException("ConnectionStrings:Owner is required for operator commands.");
        var options = config.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        var opts = ParseFlags(args.Skip(1).ToArray());
        var now = DateTimeOffset.UtcNow;

        var applied = await Migrator.MigrateAsync(owner);
        Console.Error.WriteLine($"Migrations applied: {applied}");
        if (args[0] == "migrate")
        {
            return 0;
        }

        await using var ds = NpgsqlDataSource.Create(owner);
        await using var db = ServiceCollectionExtensions.CreateContext(ds, DatabaseAccessMode.Operator);
        var hasher = new ApiKeyHasher(options.Security.ApiKeyPepper);
        var scopes = opts.TryGetValue("scopes", out var s) ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Scopes.All.ToArray();

        if (args[0] == "bootstrap")
        {
            var tenant = await Provisioning.CreateOrganizationAsync(db, Required(opts, "org"), Required(opts, "project"), now, CancellationToken.None);
            var key = await Provisioning.CreateApiKeyAsync(db, hasher, tenant.OrganizationId, tenant.ProjectId, EnvironmentKinds.Development,
                opts.GetValueOrDefault("key-name", "local development"), scopes, now, CancellationToken.None);
            PrintKey(tenant.OrganizationId, tenant.ProjectId, key);
            return 0;
        }

        var envKind = opts.GetValueOrDefault("environment", EnvironmentKinds.Development);
        if (!EnvironmentKinds.IsValid(envKind))
        {
            throw new ArgumentException("--environment must be development or production.");
        }

        var created = await Provisioning.CreateApiKeyAsync(db, hasher, Guid.Parse(Required(opts, "org")), Guid.Parse(Required(opts, "project")), envKind,
            opts.GetValueOrDefault("name", "cli"), scopes, now, CancellationToken.None);
        PrintKey(Guid.Parse(opts["org"]), Guid.Parse(opts["project"]), created);
        return 0;
    }

    private static void PrintKey(Guid org, Guid project, CreatedApiKey key)
    {
        // The key goes to stdout alone so scripts can capture it; context goes to stderr.
        Console.Error.WriteLine($"Organization: {org}\nProject:      {project}\nEnvironment:  {key.EnvironmentKind}\nScopes:       {string.Join(", ", key.Scopes)}");
        Console.Error.WriteLine("API key (shown once, store it securely):");
        Console.WriteLine(key.Key);
    }

    private static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"--{name} is required.");

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                result[args[i][2..]] = args[++i];
            }
        }

        return result;
    }
}
