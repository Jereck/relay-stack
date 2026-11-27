using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Platform.Polling;

public static class PollEventTypes
{
    public const string RecordCreated = "poll.record.created";
    public const string RecordUpdated = "poll.record.updated";
}

/// <summary>
/// Phase 0 polling experiment: GET a JSON array, identify records by a stable id field, compare a canonical
/// content hash with the last observed state and emit events for new or changed records through the same
/// EventWriter/delivery engine used by inboxes. Deletions are not detected. Not a general polling product.
/// </summary>
public sealed class PollRunner(WorkerDbFactory dbFactory, HttpClient http, PlatformOptions options, TimeProvider clock, ILogger<PollRunner> logger)
{
    public async Task<int> RunDueAsync(string workerId, CancellationToken ct)
    {
        List<PollSource> due;
        var now = clock.GetUtcNow();
        await using (var db = dbFactory.ForClaims())
        {
            // Advancing next_poll_at is the claim (worker_claim_poll_sources, SECURITY DEFINER; ADR-014).
            // A crash mid-poll simply means the source is polled again next interval.
            due = await db.PollSources
                .FromSql($"SELECT * FROM worker_claim_poll_sources({5}, {now})")
                .AsNoTracking().IgnoreQueryFilters().ToListAsync(ct);
        }

        foreach (var source in due)
        {
            try
            {
                await PollAsync(source, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Poll of source {SourceId} failed", source.Id);
                await using var db = dbFactory.For(source.OrganizationId);
                await db.PollSources.Where(p => p.Id == source.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.LastError, Redaction.Text(ex.Message, 300))
                    .SetProperty(p => p.LastPolledAt, clock.GetUtcNow()), CancellationToken.None);
            }
        }

        return due.Count;
    }

    public async Task<(int Created, int Updated)> PollAsync(PollSource source, CancellationToken ct)
    {
        await using var db = dbFactory.For(source.OrganizationId);
        var env = await db.Environments.SingleAsync(e => e.Id == source.EnvironmentId && e.OrganizationId == source.OrganizationId, ct);
        var destination = await db.Destinations.SingleAsync(d => d.Id == source.DestinationId && d.OrganizationId == source.OrganizationId, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(OutboundPolicy.AllowDevelopmentReceiver, env.Kind == EnvironmentKinds.Development);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Polling.RequestTimeoutSeconds));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, options.Polling.MaxResponseBytes, timeout.Token);

        var root = JsonNode.Parse(bytes) as JsonArray ?? throw new InvalidOperationException("Poll response must be a JSON array of objects.");
        if (root.Count > options.Polling.MaxRecordsPerPoll)
        {
            throw new InvalidOperationException($"Poll response exceeds {options.Polling.MaxRecordsPerPoll} records.");
        }

        var observed = await db.PollObservedRecords.Where(r => r.SourceId == source.Id).ToDictionaryAsync(r => r.RecordKey, ct);
        var now = clock.GetUtcNow();
        var created = 0;
        var updated = 0;
        var seenInThisPoll = new HashSet<string>(StringComparer.Ordinal);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var node in root)
        {
            if (node is not JsonObject record || RecordKey(record, source.RecordIdField) is not { } key || !seenInThisPoll.Add(key))
            {
                continue; // records without a usable id (or duplicated ids within one response) are skipped
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(record)!.ToJsonString()));
            observed.TryGetValue(key, out var previous);
            if (previous is not null && previous.ContentHash.AsSpan().SequenceEqual(hash))
            {
                previous.LastSeenAt = now;
                continue;
            }

            var isNew = previous is null;
            // The dedupe key identifies this specific transition, so re-running a poll that crashed before
            // committing observed state cannot emit the same change twice.
            var dedupe = $"{key}:{Convert.ToHexStringLower(hash)}:{(previous is null ? "new" : previous.LastChangedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            if (dedupe.Length > 600)
            {
                dedupe = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dedupe)));
            }

            var data = new JsonObject
            {
                ["poll_source_id"] = source.Id.ToString(),
                ["record_key"] = key,
                ["change"] = isNew ? "created" : "updated",
                ["record"] = record.DeepClone(),
            };
            await EventWriter.AppendAsync(db, new EventDraft(source.OrganizationId, source.ProjectId, source.EnvironmentId, env.Kind, EventSourceKinds.PollSource,
                source.Id, dedupe, isNew ? PollEventTypes.RecordCreated : PollEventTypes.RecordUpdated, data, 1, now.AddDays(source.RetentionDays)),
                new DestinationTarget(destination.Id, destination.MaxAttempts), now, ct);

            if (isNew)
            {
                created++;
                var row = new PollObservedRecord
                {
                    OrganizationId = source.OrganizationId, SourceId = source.Id, RecordKey = key, ContentHash = hash,
                    FirstSeenAt = now, LastSeenAt = now, LastChangedAt = now,
                };
                db.PollObservedRecords.Add(row);
                observed[key] = row;
            }
            else
            {
                updated++;
                previous!.ContentHash = hash;
                previous.LastSeenAt = now;
                previous.LastChangedAt = now;
            }
        }

        db.UsageRecords.Add(Usage.Record(source.OrganizationId, source.ProjectId, source.EnvironmentId, UsageMetrics.PollRequests, 1, source.Id, now));
        db.UsageRecords.Add(Usage.Record(source.OrganizationId, source.ProjectId, source.EnvironmentId, UsageMetrics.PollBytesFetched, bytes.Length, source.Id, now));
        await db.SaveChangesAsync(ct);
        await db.PollSources.Where(p => p.Id == source.Id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.LastPolledAt, now)
            .SetProperty(p => p.LastError, (string?)null), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Polled source {SourceId}: {Count} records, {Created} new, {Updated} changed", source.Id, root.Count, created, updated);
        return (created, updated);
    }

    private static string? RecordKey(JsonObject record, string field)
    {
        if (!record.TryGetPropertyValue(field, out var value) || value is null)
        {
            return null;
        }

        var key = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
        return key is { Length: > 0 and <= 512 } ? key : null;
    }

    /// <summary>Sorts object properties recursively so semantically equal records hash identically.</summary>
    internal static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonicalize).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit)
            {
                throw new InvalidOperationException($"Poll response exceeds {limit} bytes.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }
}
