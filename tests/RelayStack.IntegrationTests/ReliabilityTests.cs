using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RelayStack.IntegrationTests.Infrastructure;
using RelayStack.Platform;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Jobs;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Processing;

namespace RelayStack.IntegrationTests;

/// <summary>Part 5: retries, dead-lettering, replay, idempotency, duplicate protection, crash recovery, concurrency.</summary>
[Collection(PostgresCollection.Name)]
public class ReliabilityTests(PostgresFixture pg) : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(pg);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<(TestTenant Tenant, Guid InboxId)> PipelineAsync(int maxAttempts = 5)
    {
        var tenant = await _host.CreateTenantAsync("Reliability " + Guid.NewGuid().ToString("N")[..6]);
        var (dest, _) = await _host.CreateReceiverDestinationAsync(tenant.Dev, maxAttempts);
        return (tenant, await TestHost.CreateInboxAsync(tenant.Dev, dest));
    }

    private static async Task<string> UploadSmallAsync(TestTenant t, Guid inbox) =>
        (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(t.Dev, inbox, TestHost.Sample("timesheets_with_errors.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_event_id_until_success()
    {
        var (t, inbox) = await PipelineAsync();
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();
        _host.ReceiverState.SetMode(failNext: 3, failureStatus: 503, delayMs: 0, permanentStatus: null);

        await _host.DeliverUntilIdleAsync();

        var deliveries = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries"))["data"]!.AsArray();
        Assert.All(deliveries, d => Assert.Equal("succeeded", d!["status"]!.GetValue<string>()));
        Assert.Equal(5, deliveries.Sum(d => d!["attempt_count"]!.GetValue<int>())); // 2 deliveries + 3 retries
        var received = _host.ReceiverState.Received.ToArray();
        Assert.Equal(5, received.Length);
        Assert.Equal(2, received.Select(r => r.WebhookId).Distinct().Count());
        Assert.Equal(3, received.Count(r => r.Duplicate));

        var usage = await TestHost.GetJsonAsync(t.Dev, "/v1/usage");
        Assert.Equal(5, usage["totals"]!["delivery_attempts"]!.GetValue<long>());
        Assert.True(usage["ratios"]!["retry_overhead_bytes_ratio"]!.GetValue<double>() > 0);
    }

    [Fact]
    public async Task Exhausted_deliveries_are_dead_lettered_and_can_be_replayed_idempotently()
    {
        var (t, inbox) = await PipelineAsync(maxAttempts: 3);
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();
        _host.ReceiverState.SetMode(failNext: 1000, failureStatus: 500, delayMs: 0, permanentStatus: null);

        await _host.DeliverUntilIdleAsync();

        var failed = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries?status=failed"))["data"]!.AsArray();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, d => Assert.Equal(3, d!["attempt_count"]!.GetValue<int>()));
        var target = failed.First(d => d!["last_status_code"]!.GetValue<int>() == 500)!;
        var attempts = (await TestHost.GetJsonAsync(t.Dev, $"/v1/deliveries/{target["id"]}/attempts"))["data"]!.AsArray();
        Assert.Equal(new[] { "retryable_failure", "retryable_failure", "retryable_failure" }, attempts.Select(a => a!["outcome"]!.GetValue<string>()).ToArray());

        _host.ReceiverState.SetMode(0, 503, 0, null);
        var replay = await TestHost.ReadJsonAsync(await t.Dev.PostAsync($"/v1/deliveries/{target["id"]}/replay", null), HttpStatusCode.Created);
        Assert.Equal(target["id"]!.GetValue<string>(), replay["replay_of"]!.GetValue<string>());
        Assert.Equal(target["event_id"]!.GetValue<string>(), replay["event_id"]!.GetValue<string>());

        // A second replay request while the first is active returns the same replay.
        var again = await TestHost.ReadJsonAsync(await t.Dev.PostAsync($"/v1/deliveries/{target["id"]}/replay", null), HttpStatusCode.OK);
        Assert.Equal(replay["id"]!.GetValue<string>(), again["id"]!.GetValue<string>());

        await _host.DeliverUntilIdleAsync();
        var done = await TestHost.GetJsonAsync(t.Dev, $"/v1/deliveries/{replay["id"]}");
        Assert.Equal("succeeded", done["status"]!.GetValue<string>());
        Assert.Contains(_host.ReceiverState.Received, r => r.WebhookId == target["event_id"]!.GetValue<string>() && r.RespondedWith == 204);

        // Active deliveries cannot be replayed.
        Assert.Equal(HttpStatusCode.Created, (await t.Dev.PostAsync($"/v1/deliveries/{target["id"]}/replay", null)).StatusCode);
        var inProgress = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries?status=pending"))["data"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await t.Dev.PostAsync($"/v1/deliveries/{inProgress}/replay", null)).StatusCode);
    }

    [Fact]
    public async Task Client_errors_fail_permanently_without_retries()
    {
        var (t, inbox) = await PipelineAsync();
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();
        _host.ReceiverState.SetMode(0, 503, 0, permanentStatus: 422);

        await _host.DeliverUntilIdleAsync();

        var failed = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries?status=failed"))["data"]!.AsArray();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, d => Assert.Equal(1, d!["attempt_count"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Replay_is_refused_after_the_retention_window()
    {
        var (t, inbox) = await PipelineAsync(maxAttempts: 1);
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();
        _host.ReceiverState.SetMode(0, 503, 0, permanentStatus: 400);
        await _host.DeliverUntilIdleAsync();
        var failed = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries?status=failed"))["data"]![0]!;

        // Fast-forward retention: the sweeper purges expired payloads.
        await _host.RunRetentionAsync(DateTimeOffset.UtcNow.AddDays(8));
        var replay = await t.Dev.PostAsync($"/v1/deliveries/{failed["id"]}/replay", null);
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        var ev = await TestHost.GetJsonAsync(t.Dev, $"/v1/events/{failed["event_id"]}");
        Assert.True(ev["purged"]!.GetValue<bool>());
        var file = (await TestHost.GetJsonAsync(t.Dev, $"/v1/inboxes/{inbox}/files"))["data"]![0]!;
        Assert.Equal("expired", file["status"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(_host.StorageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_idempotency_key_returns_the_original_file_and_rejects_different_content()
    {
        var (t, inbox) = await PipelineAsync();
        var csv = TestHost.Sample("timesheets_with_errors.csv");
        var first = await TestHost.ReadJsonAsync(await TestHost.UploadAsync(t.Dev, inbox, csv, idempotencyKey: "batch-2026-11-15"), HttpStatusCode.Accepted);
        var second = await TestHost.ReadJsonAsync(await TestHost.UploadAsync(t.Dev, inbox, csv, idempotencyKey: "batch-2026-11-15"), HttpStatusCode.OK);
        Assert.Equal(first["id"]!.GetValue<string>(), second["id"]!.GetValue<string>());
        var conflict = await TestHost.UploadAsync(t.Dev, inbox, csv + "E5000,x@example.com\n", idempotencyKey: "batch-2026-11-15");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await using var db = _host.OwnerDb();
        Assert.Equal(1, await db.Files.CountAsync(f => f.InboxId == inbox));
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.OrganizationId == t.OrganizationId));
    }

    [Fact]
    public async Task Reprocessing_a_file_does_not_duplicate_events_or_deliveries()
    {
        var (t, inbox) = await PipelineAsync();
        var fileId = Guid.Parse(await UploadSmallAsync(t, inbox));
        await _host.RunJobsUntilIdleAsync();

        // Simulate a crash after events were committed but before the file was marked complete.
        await using (var db = _host.OwnerDb())
        {
            await db.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FileStatuses.Processing));
            await db.Jobs.Where(j => j.SubjectId == fileId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Pending));
        }

        await _host.RunJobsUntilIdleAsync();

        await using var check = _host.OwnerDb();
        Assert.Equal(2, await check.Events.CountAsync(e => e.SourceId == fileId));
        Assert.Equal(2, await check.Deliveries.CountAsync(d => check.Events.Any(e => e.Id == d.EventId && e.SourceId == fileId)));
        Assert.Equal(6, await check.ValidationErrors.CountAsync(v => v.FileId == fileId));
        Assert.Equal(FileStatuses.Completed, (await check.Files.SingleAsync(f => f.Id == fileId)).Status);
    }

    [Fact]
    public async Task Jobs_held_by_a_crashed_worker_are_recovered_after_the_lease_expires()
    {
        var (t, inbox) = await PipelineAsync();
        var fileId = Guid.Parse(await UploadSmallAsync(t, inbox));

        // A worker claims the job with a short lease and then "crashes" (never completes it).
        await using (var db = _host.OwnerDb())
        {
            var claimed = await JobQueue.ClaimAsync(db, "crashed-worker", 10, TimeSpan.FromMilliseconds(300), DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Contains(claimed, j => j.SubjectId == fileId);
        }

        Assert.Equal(0, await _host.Jobs.RunOnceAsync(CancellationToken.None)); // still leased: nobody else may take it
        await Task.Delay(500);
        Assert.Equal(1, await _host.Jobs.RunOnceAsync(CancellationToken.None));

        var file = await TestHost.GetJsonAsync(t.Dev, $"/v1/files/{fileId}");
        Assert.Equal("completed", file["status"]!.GetValue<string>());
        await using var check = _host.OwnerDb();
        var job = await check.Jobs.SingleAsync(j => j.SubjectId == fileId);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(JobStatuses.Succeeded, job.Status);
    }

    [Fact]
    public async Task Deliveries_held_by_a_crashed_worker_are_redelivered()
    {
        var (t, inbox) = await PipelineAsync();
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();

        await using (var db = _host.OwnerDb())
        {
            var claimed = await DeliveryDispatcher.ClaimAsync(db, "crashed-worker", 10, 10, TimeSpan.FromMilliseconds(300), DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(2, claimed.Count);
        }

        Assert.Equal(0, await _host.Dispatcher.RunOnceAsync(_host.WorkerId, CancellationToken.None));
        await Task.Delay(500);
        await _host.DeliverUntilIdleAsync();

        var deliveries = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries"))["data"]!.AsArray();
        Assert.All(deliveries, d =>
        {
            Assert.Equal("succeeded", d!["status"]!.GetValue<string>());
            Assert.Equal(2, d["attempt_count"]!.GetValue<int>()); // the lost attempt counts: at-least-once, never silently dropped
        });
    }

    [Fact]
    public async Task Per_tenant_concurrency_limit_caps_in_flight_deliveries()
    {
        var busy = await _host.CreateTenantAsync("Busy tenant");
        var quiet = await _host.CreateTenantAsync("Quiet tenant");
        var (busyDest, _) = await _host.CreateReceiverDestinationAsync(busy.Dev);
        var (quietDest, _) = await _host.CreateReceiverDestinationAsync(quiet.Dev);
        await SeedDeliveriesAsync(busy, busyDest, 12);
        await SeedDeliveriesAsync(quiet, quietDest, 2);

        await using var db = _host.OwnerDb();
        var claimed = await DeliveryDispatcher.ClaimAsync(db, "w1", batchSize: 20, maxPerOrg: 4, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(4, claimed.Count(c => c.OrganizationId == busy.OrganizationId));
        Assert.Equal(2, claimed.Count(c => c.OrganizationId == quiet.OrganizationId));

        // A second dispatcher sees the busy tenant at its limit and claims nothing more for it.
        var second = await DeliveryDispatcher.ClaimAsync(db, "w2", 20, 4, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(second);
    }

    private async Task SeedDeliveriesAsync(TestTenant t, Guid destinationId, int count)
    {
        await using var db = _host.OwnerDb();
        var env = await db.Environments.SingleAsync(e => e.OrganizationId == t.OrganizationId && e.Kind == "development");
        await using var tx = await db.Database.BeginTransactionAsync();
        var source = Guid.NewGuid();
        for (var i = 0; i < count; i++)
        {
            await EventWriter.AppendAsync(db, new EventDraft(t.OrganizationId, t.ProjectId, env.Id, env.Kind, EventSourceKinds.InboxFile, source, $"seed:{i}",
                "test.event", new System.Text.Json.Nodes.JsonObject { ["i"] = i }, 1, DateTimeOffset.UtcNow.AddDays(1)),
                new DestinationTarget(destinationId, 3), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await tx.CommitAsync();
    }

    [Fact]
    public async Task Destinations_pointing_at_internal_addresses_are_rejected()
    {
        var t = await _host.CreateTenantAsync("SSRF");
        foreach (var url in new[]
                 {
                     "https://169.254.169.254/latest/meta-data/", "https://127.0.0.1/hook", "https://10.0.0.8/hook", "https://[::1]/hook",
                     "https://localhost/hook", "http://example.com/hook", "https://user:pass@example.com/hook", "file:///etc/passwd",
                 })
        {
            var res = await t.Dev.PostAsJsonAsync("/v1/destinations", new { name = "x", url }, TestHost.Json);
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{url} was accepted");
        }

        // The development receiver exception does not apply to production environments.
        var prod = await t.Prod.PostAsJsonAsync("/v1/destinations", new { name = "x", url = _host.ReceiverWebhookUrl }, TestHost.Json);
        Assert.Equal(HttpStatusCode.BadRequest, prod.StatusCode);
    }

    [Fact]
    public async Task Delivery_to_a_name_that_resolves_to_a_blocked_address_fails_permanently()
    {
        var (t, inbox) = await PipelineAsync();
        await UploadSmallAsync(t, inbox);
        await _host.RunJobsUntilIdleAsync();

        // Re-point the destination (bypassing API validation) at a public-looking name that resolves to loopback.
        await using (var db = _host.OwnerDb())
        {
            await db.Destinations.Where(d => d.OrganizationId == t.OrganizationId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Url, $"http://localhost:{_host.ReceiverBase.Port}/webhooks"));
        }

        await _host.DeliverUntilIdleAsync();
        var failed = (await TestHost.GetJsonAsync(t.Dev, "/v1/deliveries?status=failed"))["data"]!.AsArray();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, d => Assert.Contains("Blocked by outbound policy", d!["last_error"]!.GetValue<string>()));
        Assert.Empty(_host.ReceiverState.Received);
    }
}
