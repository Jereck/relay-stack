using System.Net;
using System.Text.Json.Nodes;
using RelayStack.IntegrationTests.Infrastructure;

namespace RelayStack.IntegrationTests;

/// <summary>Part 4: the full file-to-webhook workflow through the public API, worker and dev receiver.</summary>
[Collection(PostgresCollection.Name)]
public class VerticalSliceTests(PostgresFixture pg) : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(pg);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Csv_upload_is_validated_batched_and_delivered_as_signed_webhooks()
    {
        var tenant = await _host.CreateTenantAsync("Acme Payroll");
        var project = await TestHost.GetJsonAsync(tenant.Dev, "/v1/project");
        Assert.Equal("development", project["environment"]!["kind"]!.GetValue<string>());

        var (destinationId, _) = await _host.CreateReceiverDestinationAsync(tenant.Dev);
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, destinationId);

        var upload = await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, TestHost.Sample("timesheets_with_errors.csv")), HttpStatusCode.Accepted);
        var fileId = upload["id"]!.GetValue<string>();
        Assert.Equal("received", upload["status"]!.GetValue<string>());
        Assert.Equal(64, upload["sha256"]!.GetValue<string>().Length);

        await _host.RunJobsUntilIdleAsync();

        var file = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/files/{fileId}");
        Assert.Equal("completed", file["status"]!.GetValue<string>());
        Assert.Equal(41, file["total_rows"]!.GetValue<long>());
        Assert.Equal(35, file["valid_rows"]!.GetValue<long>());
        Assert.Equal(6, file["invalid_rows"]!.GetValue<long>());

        var errors = (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/files/{fileId}/validation-errors"))["data"]!.AsArray();
        Assert.Equal(new[] { "invalid_email", "invalid_decimal", "not_allowed", "required", "invalid_date", "field_count" },
            errors.Select(e => e!["code"]!.GetValue<string>()).ToArray());
        Assert.Equal(6, errors[0]!["row_number"]!.GetValue<long>()); // data row 6 = sample index 5
        Assert.Equal("email", errors[0]!["column"]!.GetValue<string>());

        var events = (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/events?source_id={fileId}"))["data"]!.AsArray();
        Assert.Equal(new[] { "inbox.file.processed", "inbox.file.records" }, events.Select(e => e!["type"]!.GetValue<string>()).ToArray());

        var batchEvent = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/events/{events[1]!["id"]}");
        var records = batchEvent["payload"]!["data"]!["records"]!.AsArray();
        Assert.Equal(35, records.Count);
        var quoted = records.Single(r => r!["employee_id"]!.GetValue<string>() == "E1031")!;
        Assert.Equal("Rowan \"RJ\" Mendez, Jr.", quoted["full_name"]!.GetValue<string>());
        Assert.Equal(36m, quoted["hours_worked"]!.GetValue<decimal>());

        await _host.DeliverUntilIdleAsync();

        var received = _host.ReceiverState.Received.ToArray();
        Assert.Equal(2, received.Length);
        Assert.All(received, r => Assert.True(r.SignatureValid, "webhook signature must verify"));
        Assert.Equal(35, received.Single(r => r.Type == "inbox.file.records").RecordCount);
        Assert.Contains(received, r => r.Type == "inbox.file.processed");
        Assert.Equal(events.Select(e => e!["id"]!.GetValue<string>()).Order(), received.Select(r => r.WebhookId).Order());

        var deliveries = (await TestHost.GetJsonAsync(tenant.Dev, "/v1/deliveries?status=succeeded"))["data"]!.AsArray();
        Assert.Equal(2, deliveries.Count);
        var attempts = (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/deliveries/{deliveries[0]!["id"]}/attempts"))["data"]!.AsArray();
        Assert.Equal(204, Assert.Single(attempts)!["status_code"]!.GetValue<int>());

        var usage = await TestHost.GetJsonAsync(tenant.Dev, "/v1/usage");
        var totals = usage["totals"]!;
        Assert.Equal(1, totals["files_uploaded"]!.GetValue<long>());
        Assert.Equal(upload["size_bytes"]!.GetValue<long>(), totals["bytes_ingested"]!.GetValue<long>());
        Assert.Equal(41, totals["records_processed"]!.GetValue<long>());
        Assert.Equal(6, totals["records_invalid"]!.GetValue<long>());
        Assert.Equal(2, totals["events_created"]!.GetValue<long>());
        Assert.Equal(2, totals["delivery_attempts"]!.GetValue<long>());
        Assert.Equal(totals["delivery_bytes_sent"]!.GetValue<long>(), totals["delivery_bytes_delivered"]!.GetValue<long>());
        Assert.Equal(upload["size_bytes"]!.GetValue<long>() * 7, totals["storage_byte_days_committed"]!.GetValue<long>());
        Assert.True(usage["ratios"]!["delivery_bytes_sent_per_byte_ingested"]!.GetValue<double>() > 1, "JSON delivery is larger than CSV input");
    }

    [Fact]
    public async Task Large_valid_file_is_split_into_bounded_batches()
    {
        var tenant = await _host.CreateTenantAsync("Batches");
        var (destinationId, _) = await _host.CreateReceiverDestinationAsync(tenant.Dev);
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, destinationId);
        var fileId = (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, TestHost.Sample("timesheets_valid.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();
        await _host.RunJobsUntilIdleAsync();

        var file = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/files/{fileId}");
        Assert.Equal(250, file["valid_rows"]!.GetValue<long>());
        var batches = (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/events?source_id={fileId}&type=inbox.file.records"))["data"]!.AsArray();
        Assert.Equal(new[] { 50, 100, 100 }, batches.Select(b => b!["record_count"]!.GetValue<int>()).ToArray());

        await _host.DeliverUntilIdleAsync();
        Assert.Equal(250, _host.ReceiverState.Received.Where(r => r.Type == "inbox.file.records").Sum(r => r.RecordCount));
    }

    [Fact]
    public async Task List_filters_and_cursor_pagination_use_snake_case_query_parameters()
    {
        var tenant = await _host.CreateTenantAsync("Filters");
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, null);
        var first = (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, TestHost.Sample("timesheets_with_errors.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();
        var second = (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, TestHost.Sample("timesheets_valid.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();
        await _host.RunJobsUntilIdleAsync();

        var firstEvents = (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/events?source_id={first}"))["data"]!.AsArray();
        Assert.Equal(2, firstEvents.Count);
        Assert.All(firstEvents, e => Assert.Equal(first, e!["source_id"]!.GetValue<string>()));
        Assert.Equal(4, (await TestHost.GetJsonAsync(tenant.Dev, $"/v1/events?source_id={second}"))["data"]!.AsArray().Count);

        var page1 = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/inboxes/{inboxId}/files?limit=1");
        Assert.True(page1["has_more"]!.GetValue<bool>());
        Assert.Equal(second, page1["data"]![0]!["id"]!.GetValue<string>());
        var page2 = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/inboxes/{inboxId}/files?limit=1&starting_after={page1["next_cursor"]}");
        Assert.False(page2["has_more"]!.GetValue<bool>());
        Assert.Equal(first, page2["data"]![0]!["id"]!.GetValue<string>());

        var errorsPage = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/files/{first}/validation-errors?limit=2&after_row=12");
        Assert.Equal(new long[] { 18, 24 }, errorsPage["data"]!.AsArray().Select(e => e!["row_number"]!.GetValue<long>()).ToArray());
    }

    [Fact]
    public async Task Malformed_file_fails_with_a_clear_error_and_notifies_the_destination()
    {
        var tenant = await _host.CreateTenantAsync("Malformed");
        var (destinationId, _) = await _host.CreateReceiverDestinationAsync(tenant.Dev);
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, destinationId);
        var fileId = (await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, TestHost.Sample("timesheets_malformed.csv")), HttpStatusCode.Accepted))["id"]!.GetValue<string>();
        await _host.RunJobsUntilIdleAsync();

        var file = await TestHost.GetJsonAsync(tenant.Dev, $"/v1/files/{fileId}");
        Assert.Equal("failed", file["status"]!.GetValue<string>());
        Assert.Equal("unterminated_quote", file["error_code"]!.GetValue<string>());

        await _host.DeliverUntilIdleAsync();
        var failed = Assert.Single(_host.ReceiverState.Received.ToArray());
        Assert.Equal("inbox.file.failed", failed.Type);
    }

    [Fact]
    public async Task Upload_limits_and_content_type_are_enforced()
    {
        var tenant = await _host.CreateTenantAsync("Limits");
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, null, maxFileBytes: 1024);

        var tooLarge = await TestHost.UploadAsync(tenant.Dev, inboxId, "employee_id\n" + new string('x', 2000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await TestHost.UploadAsync(tenant.Dev, inboxId, "a", contentType: "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TestHost.UploadAsync(tenant.Dev, inboxId, "")).StatusCode);

        // Nothing was persisted for rejected uploads.
        Assert.Empty((await TestHost.GetJsonAsync(tenant.Dev, $"/v1/inboxes/{inboxId}/files"))["data"]!.AsArray());
        Assert.Empty(Directory.Exists(_host.StorageRoot) ? Directory.GetFiles(_host.StorageRoot, "*", SearchOption.AllDirectories) : []);

        var badSchema = await tenant.Dev.PostAsync("/v1/inboxes", new StringContent("""{"name":"x","schema":{"columns":[{"name":"a","type":"money"}]}}""", System.Text.Encoding.UTF8, "application/json"));
        var problem = await TestHost.ReadJsonAsync(badSchema, HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", problem["code"]!.GetValue<string>());
        Assert.Contains("unknown type 'money'", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task File_name_is_sanitized()
    {
        var tenant = await _host.CreateTenantAsync("Names");
        var inboxId = await TestHost.CreateInboxAsync(tenant.Dev, null);
        var res = await TestHost.ReadJsonAsync(await TestHost.UploadAsync(tenant.Dev, inboxId, "employee_id\nE1\n", filename: "../../etc/passwd"), HttpStatusCode.Accepted);
        Assert.Equal("passwd", res["original_name"]!.GetValue<string>());
        var fileId = Guid.Parse(res["id"]!.GetValue<string>());
        await using var db = _host.OwnerDb();
        Assert.StartsWith($"org/{tenant.OrganizationId:N}/", db.Files.Single(f => f.Id == fileId).StorageKey);
    }
}
