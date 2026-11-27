using Npgsql;
using RelayStack.Platform.Persistence;
using Testcontainers.PostgreSql;

namespace RelayStack.IntegrationTests.Infrastructure;

public sealed record TestDatabase(string Name, string OwnerConnectionString, string AppConnectionString, string WorkerConnectionString);

/// <summary>
/// One disposable Postgres container for the whole run (Testcontainers). Migrations are applied once to a
/// template database; every test gets its own database cloned from the template, so workers that claim
/// work across all tenants cannot see another test's rows. The API and worker roles are given test passwords
/// so tests connect exactly as production processes would.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string AppPassword = "app_test_password";
    private const string WorkerPassword = "worker_test_password";
    private readonly string _template = "rs_template_" + Guid.NewGuid().ToString("N")[..12];
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private string _admin = "";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _admin = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false }.ConnectionString;
        await ExecAsync(_admin, $"CREATE DATABASE {_template}");
        await Migrator.MigrateAsync(WithDatabase(_admin, _template));
        await ExecAsync(_admin, $"ALTER ROLE relaystack_app LOGIN PASSWORD '{AppPassword}'; ALTER ROLE relaystack_worker LOGIN PASSWORD '{WorkerPassword}';");
    }

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "rs_" + Guid.NewGuid().ToString("N")[..16];
        await ExecAsync(_admin, $"CREATE DATABASE {name} TEMPLATE {_template}");
        var owner = WithDatabase(_container.GetConnectionString(), name);
        return new TestDatabase(name, owner,
            new NpgsqlConnectionStringBuilder(owner) { Username = "relaystack_app", Password = AppPassword }.ConnectionString,
            new NpgsqlConnectionStringBuilder(owner) { Username = "relaystack_worker", Password = WorkerPassword }.ConnectionString);
    }

    public Task DropDatabaseAsync(TestDatabase db) => ExecAsync(_admin, $"DROP DATABASE IF EXISTS {db.Name} WITH (FORCE)");

    public async ValueTask DisposeAsync()
    {
        await ExecAsync(_admin, $"DROP DATABASE IF EXISTS {_template} WITH (FORCE)");
        await _container.DisposeAsync();
    }

    private static string WithDatabase(string cs, string db) => new NpgsqlConnectionStringBuilder(cs) { Database = db }.ConnectionString;

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
