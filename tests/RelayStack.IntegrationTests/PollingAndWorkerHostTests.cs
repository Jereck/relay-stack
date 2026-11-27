using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using RelayStack.IntegrationTests.Infrastructure;
using RelayStack.Platform;
using RelayStack.Platform.Polling;

namespace RelayStack.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PollingAndWorkerHostTests(PostgresFixture pg) : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(pg);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Part 9: a polling input reuses EventWriter and the delivery engine without changes.</summary>
    [Fact]
    public async Task Polling_emits_events_only_for_new_or_changed_records_through_the_same_delivery_engine()
    {
        var t = await _host.CreateTenantAsync("Polling");
        var (dest, _) = await _host.CreateReceiverDestinationAsync(t.Dev);
        var created = await t.Dev.PostAsJsonAsync("/v1/poll-sources", new
        {
            name = "synthetic employees",
            url = new Uri(_host.ReceiverBase, "/synthetic/employees").ToString(),
            record_id_field = "id",
            interval_seconds = 60,
            destination_id = dest,
        }, TestHost.Json);
        var source = await TestHost.ReadJsonAsync(created, HttpStatusCode.Created);
        var sourceId = Guid.Parse(source["id"]!.GetValue<string>());

        async Task<(int Created, int Updated)> Poll()
        {
            await using var db = _host.OwnerDb();
            var s = await db.PollSources.SingleAsync(p => p.Id == sourceId);
            return await _host.Poller.PollAsync(s, CancellationToken.None);
        }

        Assert.Equal((3, 0), await Poll());
        Assert.Equal((0, 0), await Poll());          // unchanged content: no events
        await new HttpClient().PostAsync(new Uri(_host.ReceiverBase, "/synthetic/employees/mutate"), null);
        Assert.Equal((1, 1), await Poll());          // one new record, one changed title

        var events = (await TestHost.GetJsonAsync(t.Dev, $"/v1/events?source_id={sourceId}"))["data"]!.AsArray();
        Assert.Equal(5, events.Count);
        Assert.Equal(4, events.Count(e => e!["type"]!.GetValue<string>() == PollEventTypes.RecordCreated));
        Assert.Equal(1, events.Count(e => e!["type"]!.GetValue<string>() == PollEventTypes.RecordUpdated));

        await _host.DeliverUntilIdleAsync();
        var received = _host.ReceiverState.Received.Where(r => r.Type!.StartsWith("poll.")).ToArray();
        Assert.Equal(5, received.Length);
        Assert.All(received, r => Assert.True(r.SignatureValid));
        var usage = await TestHost.GetJsonAsync(t.Dev, "/v1/usage");
        Assert.Equal(3, usage["totals"]!["poll_requests"]!.GetValue<long>());
    }

    [Fact]
    public async Task Due_poll_sources_are_claimed_by_the_scheduler()
    {
        var t = await _host.CreateTenantAsync("Poll schedule");
        var (dest, _) = await _host.CreateReceiverDestinationAsync(t.Dev);
        await TestHost.ReadJsonAsync(await t.Dev.PostAsJsonAsync("/v1/poll-sources", new
        {
            name = "s", url = new Uri(_host.ReceiverBase, "/synthetic/employees").ToString(), record_id_field = "id", interval_seconds = 3600, destination_id = dest,
        }, TestHost.Json), HttpStatusCode.Created);

        Assert.Equal(1, await _host.Poller.RunDueAsync("w", CancellationToken.None));
        Assert.Equal(0, await _host.Poller.RunDueAsync("w", CancellationToken.None)); // next poll is an hour away
    }

    /// <summary>The real worker host (all background services running) processes an upload end to end.</summary>
    [Fact]
    public async Task Running_worker_host_processes_and_delivers_without_manual_steps()
    {
        var t = await _host.CreateTenantAsync("Live worker");
        var (dest, _) = await _host.CreateReceiverDestinationAsync(t.Dev);
        var inbox = await TestHost.CreateInboxAsync(t.Dev, dest);
        var fileId = (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(t.Dev, inbox, TestHost.Sample("timesheets_valid.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();

        await _host.Worker.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && _host.ReceiverState.Received.Count < 4)
            {
                await Task.Delay(100);
            }
        }
        finally
        {
            await _host.Worker.StopAsync();
        }

        var file = await TestHost.GetJsonAsync(t.Dev, $"/v1/files/{fileId}");
        Assert.Equal("completed", file["status"]!.GetValue<string>());
        Assert.Equal(4, _host.ReceiverState.Received.Count); // 3 batches + completion
        await using var db = _host.OwnerDb();
        Assert.False(await db.Deliveries.AnyAsync(d => d.OrganizationId == t.OrganizationId && d.Status != DeliveryStatuses.Succeeded));
    }
}
