using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayStack.IntegrationTests.Infrastructure;
using RelayStack.Platform.Security;

namespace RelayStack.IntegrationTests;

/// <summary>Automated cross-tenant access tests (Part 6). Every resource of tenant B must be invisible to tenant A.</summary>
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture pg) : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(pg);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Tenant_cannot_read_or_act_on_another_tenants_resources()
    {
        var a = await _host.CreateTenantAsync("Tenant A");
        var b = await _host.CreateTenantAsync("Tenant B");

        // Tenant B builds a full pipeline and produces a delivery.
        var (bDest, _) = await _host.CreateReceiverDestinationAsync(b.Dev);
        var bInbox = await TestHost.CreateInboxAsync(b.Dev, bDest);
        var upload = await TestHost.ReadJsonAsync(await TestHost.UploadAsync(b.Dev, bInbox, TestHost.Sample("timesheets_with_errors.csv")), HttpStatusCode.Accepted);
        var bFile = upload["id"]!.GetValue<string>();
        await _host.RunJobsUntilIdleAsync();
        await _host.DeliverUntilIdleAsync();
        var bEvent = (await TestHost.GetJsonAsync(b.Dev, $"/v1/events?source_id={bFile}"))["data"]![0]!["id"]!.GetValue<string>();
        var bDelivery = (await TestHost.GetJsonAsync(b.Dev, "/v1/deliveries"))["data"]![0]!["id"]!.GetValue<string>();

        // Tenant A sees nothing of it, by id or in lists.
        foreach (var path in new[]
                 {
                     $"/v1/inboxes/{bInbox}", $"/v1/destinations/{bDest}", $"/v1/files/{bFile}", $"/v1/files/{bFile}/validation-errors",
                     $"/v1/inboxes/{bInbox}/files", $"/v1/events/{bEvent}", $"/v1/events/{bEvent}/deliveries", $"/v1/deliveries/{bDelivery}",
                     $"/v1/deliveries/{bDelivery}/attempts",
                 })
        {
            var res = await a.Dev.GetAsync(path);
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{path} returned {(int)res.StatusCode} to another tenant");
        }

        Assert.Empty((await TestHost.GetJsonAsync(a.Dev, "/v1/inboxes"))["data"]!.AsArray());
        Assert.Empty((await TestHost.GetJsonAsync(a.Dev, "/v1/destinations"))["data"]!.AsArray());
        Assert.Empty((await TestHost.GetJsonAsync(a.Dev, "/v1/events"))["data"]!.AsArray());
        Assert.Empty((await TestHost.GetJsonAsync(a.Dev, "/v1/deliveries"))["data"]!.AsArray());
        Assert.Equal(0, (await TestHost.GetJsonAsync(a.Dev, "/v1/usage"))["totals"]!["files_uploaded"]!.GetValue<long>());

        // Writes are refused too.
        Assert.Equal(HttpStatusCode.NotFound, (await TestHost.UploadAsync(a.Dev, bInbox, "employee_id\nE1\n")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Dev.PostAsync($"/v1/deliveries/{bDelivery}/replay", null)).StatusCode);
        var inboxWithForeignDestination = await a.Dev.PostAsJsonAsync("/v1/inboxes", new
        {
            name = "steal", destination_id = bDest, schema = new { columns = new[] { new { name = "x", type = "string" } } },
        }, TestHost.Json);
        Assert.Equal(HttpStatusCode.BadRequest, inboxWithForeignDestination.StatusCode);
    }

    [Fact]
    public async Task Development_key_cannot_see_production_environment_resources()
    {
        var t = await _host.CreateTenantAsync("Env split");
        var prodInbox = await TestHost.CreateInboxAsync(t.Prod, null);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Dev.GetAsync($"/v1/inboxes/{prodInbox}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TestHost.UploadAsync(t.Dev, prodInbox, "employee_id\nE1\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Prod.GetAsync($"/v1/inboxes/{prodInbox}")).StatusCode);
        var project = await TestHost.GetJsonAsync(t.Prod, "/v1/project");
        Assert.Equal("production", project["environment"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Row_level_security_isolates_tenants_even_for_raw_sql()
    {
        var a = await _host.CreateTenantAsync("RLS A");
        var b = await _host.CreateTenantAsync("RLS B");
        await TestHost.CreateInboxAsync(a.Dev, null);
        await TestHost.CreateInboxAsync(b.Dev, null);

        await using var conn = new NpgsqlConnection(_host.Database.AppConnectionString);
        await conn.OpenAsync();

        async Task<long> Count(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        // No tenant set: the API role sees nothing at all (fail closed).
        Assert.Equal(0, await Count("SELECT count(*) FROM inboxes"));
        Assert.Equal(0, await Count("SELECT count(*) FROM organizations"));

        await using (var set = new NpgsqlCommand($"SELECT set_config('relaystack.organization_id', '{a.OrganizationId}', false)", conn))
        {
            await set.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await Count("SELECT count(*) FROM inboxes"));
        Assert.Equal(0, await Count($"SELECT count(*) FROM inboxes WHERE organization_id = '{b.OrganizationId}'"));
        // The API role has no privilege on api_keys at all; keys are only reachable through auth_lookup_api_key().
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Count("SELECT count(*) FROM api_keys"))).SqlState);

        // Writing a row for another tenant is rejected by the policy's WITH CHECK.
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO audit_log (organization_id, actor_type, actor_id, action, target_type, target_id) VALUES ('{b.OrganizationId}', 't', 't', 't', 't', 't')", conn);
            await insert.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Authentication_and_scopes_are_enforced()
    {
        var full = await _host.CreateTenantAsync("Scopes");
        var readOnly = await _host.CreateTenantAsync("Read only", [Scopes.InboxesRead]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(null).GetAsync("/v1/inboxes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client("rs_dev_aaaaaaaaaaaa_" + new string('A', 43)).GetAsync("/v1/inboxes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(full.DevKey[..^1] + (full.DevKey[^1] == 'A' ? 'B' : 'A')).GetAsync("/v1/inboxes")).StatusCode);
        // A dev key relabelled as prod fails: the environment marker is checked against the database.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(full.DevKey.Replace("rs_dev_", "rs_prod_")).GetAsync("/v1/inboxes")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await readOnly.Dev.GetAsync("/v1/inboxes")).StatusCode);
        var denied = await readOnly.Dev.PostAsJsonAsync("/v1/inboxes", new { name = "x" }, TestHost.Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Dev.GetAsync("/v1/deliveries")).StatusCode);

        await using var db = _host.OwnerDb();
        await db.ApiKeys.Where(k => k.OrganizationId == full.OrganizationId).ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Unauthorized, (await full.Dev.GetAsync("/v1/inboxes")).StatusCode);
    }
}
