using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RelayStack.Api;
using RelayStack.DevReceiver;
using RelayStack.Platform;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Polling;
using RelayStack.Platform.Retention;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;
using RelayStack.Worker;

namespace RelayStack.IntegrationTests.Infrastructure;

public sealed record TestTenant(Guid OrganizationId, Guid ProjectId, string DevKey, string ProdKey, HttpClient Dev, HttpClient Prod);

/// <summary>
/// Runs the real API (Kestrel on a random loopback port), the dev receiver, and a worker host whose loops
/// are driven step by step so tests are deterministic. Everything talks to the shared Postgres fixture.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static readonly string Pepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly List<HttpClient> _clients = [];

    private readonly NpgsqlDataSource _ownerDataSource;

    private TestHost(PostgresFixture pg, TestDatabase database, WebApplication api, WebApplication receiver, IHost worker, string storageRoot, Uri apiBase, Uri receiverBase, Dictionary<string, string?> config)
    {
        Postgres = pg;
        Database = database;
        _ownerDataSource = NpgsqlDataSource.Create(database.OwnerConnectionString);
        Api = api;
        Receiver = receiver;
        Worker = worker;
        StorageRoot = storageRoot;
        ApiBase = apiBase;
        ReceiverBase = receiverBase;
        Config = config;
    }

    public PostgresFixture Postgres { get; }
    public TestDatabase Database { get; }
    public WebApplication Api { get; }
    public WebApplication Receiver { get; }
    public IHost Worker { get; }
    public string StorageRoot { get; }
    public Uri ApiBase { get; }
    public Uri ReceiverBase { get; }
    public Dictionary<string, string?> Config { get; }
    public ReceiverState ReceiverState => Receiver.Services.GetRequiredService<ReceiverState>();
    public string ReceiverWebhookUrl => new Uri(ReceiverBase, "/webhooks").ToString();
    public JobWorkerService Jobs => Worker.Services.GetServices<IHostedService>().OfType<JobWorkerService>().Single();
    public DeliveryDispatcher Dispatcher => Worker.Services.GetRequiredService<DeliveryDispatcher>();
    public PollRunner Poller => Worker.Services.GetRequiredService<PollRunner>();
    public string WorkerId => Worker.Services.GetRequiredService<WorkerIdentity>().Id;

    public static async Task<TestHost> StartAsync(PostgresFixture pg, Action<Dictionary<string, string?>>? configure = null)
    {
        var database = await pg.CreateDatabaseAsync();
        var receiver = ReceiverApp.Build(["--environment=Development", "--urls=http://127.0.0.1:0"], b => b.Logging.SetMinimumLevelWarning());
        await receiver.StartAsync();
        var receiverBase = new Uri(Address(receiver));

        var storageRoot = Path.Combine(Path.GetTempPath(), "relaystack-tests", Guid.NewGuid().ToString("N"));
        var config = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = database.AppConnectionString,
            ["ConnectionStrings:Worker"] = database.WorkerConnectionString,
            ["RelayStack:Security:ApiKeyPepper"] = Pepper,
            ["RelayStack:Security:EncryptionKeys:1"] = EncryptionKey,
            ["RelayStack:Security:CurrentEncryptionKeyId"] = "1",
            ["RelayStack:Storage:RootPath"] = storageRoot,
            ["RelayStack:Delivery:DevelopmentReceiverAllowlist:0"] = $"127.0.0.1:{receiverBase.Port}",
            ["RelayStack:Delivery:RetryBaseDelaySeconds"] = "0.05",
            ["RelayStack:Delivery:RetryMaxDelaySeconds"] = "0.2",
            ["RelayStack:Delivery:LeaseSeconds"] = "30",
            ["RelayStack:Processing:BatchMaxRecords"] = "100",
            ["RelayStack:Limits:ApiRequestsPerSecondPerKey"] = "1000",
            ["RelayStack:Limits:ApiBurstPerKey"] = "1000",
            ["RelayStack:Limits:UploadsPerMinutePerKey"] = "600",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        };
        configure?.Invoke(config);

        var api = ApiApp.Build(["--environment=Development", "--urls=http://127.0.0.1:0"], b => b.Configuration.AddInMemoryCollection(config));
        await api.StartAsync();
        var apiBase = new Uri(Address(api));

        var worker = WorkerApp.Build(["--environment=Development"], b => b.Configuration.AddInMemoryCollection(config));
        return new TestHost(pg, database, api, receiver, worker, storageRoot, apiBase, receiverBase, config);
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    public HttpClient Client(string? apiKey)
    {
        var c = new HttpClient { BaseAddress = ApiBase };
        if (apiKey is not null)
        {
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        _clients.Add(c);
        return c;
    }

    public RelayStackDbContext OwnerDb() => ServiceCollectionExtensions.CreateContext(_ownerDataSource, DatabaseAccessMode.Operator);

    public async Task<TestTenant> CreateTenantAsync(string name, IReadOnlyCollection<string>? scopes = null)
    {
        await using var db = OwnerDb();
        var hasher = new ApiKeyHasher(Pepper);
        var now = DateTimeOffset.UtcNow;
        var t = await Provisioning.CreateOrganizationAsync(db, name, name + " project", now, CancellationToken.None);
        var dev = await Provisioning.CreateApiKeyAsync(db, hasher, t.OrganizationId, t.ProjectId, EnvironmentKinds.Development, "dev", scopes ?? Scopes.All.ToArray(), now, CancellationToken.None);
        var prod = await Provisioning.CreateApiKeyAsync(db, hasher, t.OrganizationId, t.ProjectId, EnvironmentKinds.Production, "prod", scopes ?? Scopes.All.ToArray(), now, CancellationToken.None);
        return new TestTenant(t.OrganizationId, t.ProjectId, dev.Key, prod.Key, Client(dev.Key), Client(prod.Key));
    }

    // ---- API helpers -------------------------------------------------------------------------------

    public async Task<(Guid Id, string Secret)> CreateReceiverDestinationAsync(HttpClient client, int maxAttempts = 5)
    {
        var res = await client.PostAsJsonAsync("/v1/destinations", new { name = "dev receiver", url = ReceiverWebhookUrl, max_attempts = maxAttempts, timeout_seconds = 5 }, Json);
        var body = await ReadJsonAsync(res, System.Net.HttpStatusCode.Created);
        var secret = body["signing_secret"]!.GetValue<string>();
        ReceiverState.Secrets.Add(secret);
        return (Guid.Parse(body["id"]!.GetValue<string>()), secret);
    }

    public static async Task<Guid> CreateInboxAsync(HttpClient client, Guid? destinationId, string? schemaJson = null, long? maxFileBytes = null)
    {
        var schema = JsonNode.Parse(schemaJson ?? await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "schema.timesheets.json")));
        var res = await client.PostAsJsonAsync("/v1/inboxes", new JsonObject
        {
            ["name"] = "timesheets",
            ["destination_id"] = destinationId?.ToString(),
            ["schema"] = schema,
            ["max_file_bytes"] = maxFileBytes,
        });
        var body = await ReadJsonAsync(res, System.Net.HttpStatusCode.Created);
        return Guid.Parse(body["id"]!.GetValue<string>());
    }

    public static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid inboxId, string csv, string? idempotencyKey = null, string contentType = "text/csv", string filename = "timesheets.csv")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/inboxes/{inboxId}/files?filename={Uri.EscapeDataString(filename)}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv)),
        };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(req);
    }

    public static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage res, System.Net.HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"Expected {(int)expected} but got {(int)res.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    public static async Task<JsonObject> GetJsonAsync(HttpClient client, string path) =>
        await ReadJsonAsync(await client.GetAsync(path), System.Net.HttpStatusCode.OK);

    // ---- Worker stepping -----------------------------------------------------------------------------

    public async Task RunJobsUntilIdleAsync()
    {
        for (var i = 0; i < 20 && await Jobs.RunOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    /// <summary>Runs the dispatcher until no deliveries are pending or in flight (retry delays are tiny in tests).</summary>
    public async Task DeliverUntilIdleAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var n = await Dispatcher.RunOnceAsync(WorkerId, CancellationToken.None);
            if (n == 0)
            {
                await using var db = OwnerDb();
                if (!await db.Deliveries.AnyAsync(d => d.Status == DeliveryStatuses.Pending || d.Status == DeliveryStatuses.InFlight))
                {
                    return;
                }

                await Task.Delay(50);
            }
        }

        throw new TimeoutException("Deliveries did not settle in time.");
    }

    /// <summary>Runs retention exactly as the worker host does: relaystack_worker role, one organization at a time.</summary>
    public Task RunRetentionAsync(DateTimeOffset now) =>
        Worker.Services.GetRequiredService<RetentionSweeper>().RunOnceAsync(WorkerDbs, now, 1000, CancellationToken.None);

    /// <summary>The worker host's context factory (relaystack_worker role, subject to RLS).</summary>
    public WorkerDbFactory WorkerDbs => Worker.Services.GetRequiredService<WorkerDbFactory>();

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients)
        {
            c.Dispose();
        }

        await Api.StopAsync();
        await Receiver.StopAsync();
        await Api.DisposeAsync();
        await Receiver.DisposeAsync();
        Worker.Dispose();
        await _ownerDataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await Postgres.DropDatabaseAsync(Database);
        try
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

internal static class LoggingExtensions
{
    // Uses AddFilter to avoid an ambiguous LoggingBuilderExtensions reference in this assembly.
    public static void SetMinimumLevelWarning(this Microsoft.Extensions.Logging.ILoggingBuilder logging) =>
        logging.AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Warning);
}
