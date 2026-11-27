using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayStack.IntegrationTests.Infrastructure;
using RelayStack.Platform;
using RelayStack.Platform.Persistence;

namespace RelayStack.IntegrationTests;

/// <summary>
/// The worker role is subject to row-level security (ADR-014). It finds work only through the SECURITY DEFINER claim
/// functions, and every other query is limited to the organization of the item being processed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkerScopingTests(PostgresFixture pg) : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(pg);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Two tenants, each with a processed upload, so every worker-touched table has rows for both.</summary>
    private async Task<(TestTenant A, TestTenant B)> TwoTenantsWithDataAsync()
    {
        var a = await _host.CreateTenantAsync("Worker A");
        var b = await _host.CreateTenantAsync("Worker B");
        foreach (var t in new[] { a, b })
        {
            var (dest, _) = await _host.CreateReceiverDestinationAsync(t.Dev);
            var inbox = await TestHost.CreateInboxAsync(t.Dev, dest);
            await TestHost.ReadJsonAsync(await TestHost.UploadAsync(t.Dev, inbox, TestHost.Sample("timesheets_with_errors.csv")), HttpStatusCode.Accepted);
        }

        await _host.RunJobsUntilIdleAsync();
        await _host.DeliverUntilIdleAsync();
        return (a, b);
    }

    [Fact]
    public async Task Worker_role_sees_only_the_scoped_organization_even_for_raw_sql()
    {
        var (a, b) = await TwoTenantsWithDataAsync();

        await using var conn = new NpgsqlConnection(_host.Database.WorkerConnectionString);
        await conn.OpenAsync();

        async Task<long> Count(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        string[] tables = ["organizations", "projects", "environments", "webhook_destinations", "inboxes", "files", "validation_errors",
            "jobs", "events", "deliveries", "delivery_attempts", "usage_records", "audit_log"];

        // No organization set: nothing is visible (fail closed). Previously the worker saw every tenant here.
        foreach (var t in tables)
        {
            Assert.True(await Count($"SELECT count(*) FROM {t}") == 0, $"{t} visible to an unscoped worker connection");
        }

        await using (var set = new NpgsqlCommand($"SELECT set_config('relaystack.organization_id', '{a.OrganizationId}', false)", conn))
        {
            await set.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await Count("SELECT count(*) FROM organizations"));
        Assert.True(await Count("SELECT count(*) FROM deliveries") > 0);
        foreach (var t in tables.Where(t => t != "organizations"))
        {
            Assert.True(await Count($"SELECT count(*) FROM {t} WHERE organization_id = '{b.OrganizationId}'") == 0, $"{t} leaks tenant B to a worker scoped to A");
        }

        // Updates to another tenant's rows match nothing; inserts for another tenant are rejected by WITH CHECK.
        await using (var update = new NpgsqlCommand($"UPDATE deliveries SET last_error = 'x' WHERE organization_id = '{b.OrganizationId}'", conn))
        {
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO audit_log (organization_id, actor_type, actor_id, action, target_type, target_id) VALUES ('{b.OrganizationId}', 't', 't', 't', 't', 't')", conn);
            await insert.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", ex.SqlState);

        // The worker still has no privilege on API keys at all.
        await using var keys = new NpgsqlCommand("SELECT count(*) FROM api_keys", conn);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => keys.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task Worker_contexts_are_limited_to_their_organization()
    {
        var (a, b) = await TwoTenantsWithDataAsync();

        await using (var unscoped = _host.WorkerDbs.ForClaims())
        {
            Assert.Equal(0, await unscoped.Files.CountAsync());
            Assert.Equal(0, await unscoped.Files.IgnoreQueryFilters().CountAsync()); // RLS still applies without EF filters
        }

        await using var scopedToA = _host.WorkerDbs.For(a.OrganizationId);
        Assert.True(await scopedToA.Files.CountAsync() > 0);
        Assert.Equal(0, await scopedToA.Files.CountAsync(f => f.OrganizationId == b.OrganizationId));
        Assert.Equal(0, await scopedToA.Events.IgnoreQueryFilters().CountAsync(e => e.OrganizationId == b.OrganizationId));
        Assert.Equal(0, await scopedToA.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM deliveries WHERE organization_id = {b.OrganizationId}").SingleAsync());

        // A scoped context cannot be moved to another organization, and only worker contexts can be scoped explicitly.
        Assert.Throws<InvalidOperationException>(() => scopedToA.ScopeToOrganization(b.OrganizationId));
        await using var op = _host.OwnerDb();
        Assert.Throws<InvalidOperationException>(() => op.ScopeToOrganization(a.OrganizationId));
    }

    [Fact]
    public async Task Scoping_after_the_connection_is_open_is_refused()
    {
        await using var db = _host.WorkerDbs.ForClaims();
        await db.Database.OpenConnectionAsync();
        Assert.Throws<InvalidOperationException>(() => db.ScopeToOrganization(Guid.NewGuid()));
    }

    [Fact]
    public async Task Claim_functions_are_not_callable_by_the_api_role()
    {
        await using var conn = new NpgsqlConnection(_host.Database.AppConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "SELECT * FROM worker_claim_jobs('x', 1, now(), now())",
                     "SELECT * FROM worker_claim_deliveries('x', 1, 1, now(), now())",
                     "SELECT * FROM worker_claim_poll_sources(1, now())",
                     "SELECT * FROM worker_orgs_with_expired_payloads(now(), 1)",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState == "42501", $"{sql} returned {ex.SqlState}");
        }
    }

    [Fact]
    public async Task Retention_sweeps_every_organization_through_scoped_contexts()
    {
        var (a, b) = await TwoTenantsWithDataAsync();

        await _host.RunRetentionAsync(DateTimeOffset.UtcNow.AddDays(31));

        await using var db = _host.OwnerDb();
        foreach (var org in new[] { a.OrganizationId, b.OrganizationId })
        {
            Assert.True(await db.Events.AnyAsync(e => e.OrganizationId == org));
            Assert.Equal(0, await db.Events.CountAsync(e => e.OrganizationId == org && e.PurgedAt == null));
            Assert.Equal(0, await db.Files.CountAsync(f => f.OrganizationId == org && f.Status != FileStatuses.Expired));
        }
    }
}
