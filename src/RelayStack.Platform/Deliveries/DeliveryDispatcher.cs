using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Platform.Deliveries;

public sealed record ClaimedDelivery(Guid Id, Guid OrganizationId, Guid EventId, Guid DestinationId, int AttemptNumber, int MaxAttempts);

/// <summary>
/// Claims due deliveries and performs one HTTP attempt for each. Delivery is at-least-once:
/// a crash after the receiver accepted a request but before the outcome is committed causes a re-send
/// with the same webhook-id. See docs/DELIVERY_SEMANTICS.md.
/// </summary>
public sealed class DeliveryDispatcher(
    WorkerDbFactory dbFactory,
    HttpClient http,
    SecretProtector secrets,
    PlatformOptions options,
    TimeProvider clock,
    ILogger<DeliveryDispatcher> logger)
{
    private static readonly ActivitySource Activity = new("RelayStack.Delivery");
    private readonly RetryPolicy _retry = new(options.Delivery.RetryBaseDelaySeconds, options.Delivery.RetryMaxDelaySeconds);

    /// <summary>
    /// Claims up to ClaimBatchSize due deliveries while keeping each organization below
    /// MaxConcurrentPerOrganization in-flight deliveries (a soft limit under concurrent dispatchers; ADR-008).
    /// Deliveries whose lease expired (crashed worker) are reclaimed.
    /// </summary>
    public static async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(RelayStackDbContext db, string workerId, int batchSize, int maxPerOrg,
        TimeSpan lease, DateTimeOffset now, CancellationToken ct)
    {
        var leaseUntil = now + lease;
        // Cross-tenant by design: worker_claim_deliveries (SECURITY DEFINER) applies the per-organization cap,
        // leases due rows and returns only those (ADR-014).
        var rows = await db.Deliveries
            .FromSql($"SELECT * FROM worker_claim_deliveries({workerId}, {batchSize}, {maxPerOrg}, {leaseUntil}, {now})")
            .AsNoTracking().IgnoreQueryFilters().ToListAsync(ct);

        return rows.Select(d => new ClaimedDelivery(d.Id, d.OrganizationId, d.EventId, d.DestinationId, d.AttemptCount, d.MaxAttempts)).ToList();
    }

    public async Task<int> RunOnceAsync(string workerId, CancellationToken ct)
    {
        IReadOnlyList<ClaimedDelivery> claimed;
        await using (var db = dbFactory.ForClaims())
        {
            claimed = await ClaimAsync(db, workerId, options.Delivery.ClaimBatchSize, options.Delivery.MaxConcurrentPerOrganization,
                TimeSpan.FromSeconds(options.Delivery.LeaseSeconds), clock.GetUtcNow(), ct);
        }

        if (claimed.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(claimed, new ParallelOptions { MaxDegreeOfParallelism = options.Delivery.WorkerParallelism, CancellationToken = ct },
            async (d, token) =>
            {
                try
                {
                    await AttemptAsync(d, workerId, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The lease will expire and another attempt will be made; never lose the delivery.
                    logger.LogError(ex, "Unexpected error delivering {DeliveryId}", d.Id);
                }
            });
        return claimed.Count;
    }

    private async Task AttemptAsync(ClaimedDelivery claim, string workerId, CancellationToken ct)
    {
        using var activity = Activity.StartActivity("deliver_webhook");
        activity?.SetTag("relaystack.delivery_id", claim.Id);
        activity?.SetTag("relaystack.attempt", claim.AttemptNumber);

        await using var db = dbFactory.For(claim.OrganizationId);
        var target = await (
            from ev in db.Events
            join dest in db.Destinations on new { ev.OrganizationId, Id = claim.DestinationId } equals new { dest.OrganizationId, dest.Id }
            join env in db.Environments on dest.EnvironmentId equals env.Id
            where ev.Id == claim.EventId && ev.OrganizationId == claim.OrganizationId
            select new { Event = ev, Destination = dest, EnvironmentKind = env.Kind }).SingleAsync(ct);

        var started = clock.GetUtcNow();
        var sw = Stopwatch.StartNew();
        AttemptResult result;
        if (target.Event.Body is null)
        {
            result = new AttemptResult(AttemptClassification.Permanent, null, "Event payload has expired and was purged.", 0, 0, null);
        }
        else if (!target.Destination.Enabled)
        {
            result = new AttemptResult(AttemptClassification.Permanent, null, "Destination is disabled.", 0, 0, null);
        }
        else
        {
            var secret = secrets.Unprotect(target.Destination.SigningSecretCiphertext, SecretPurposes.WebhookSigningSecret, claim.OrganizationId);
            result = await SendAsync(target.Destination, target.EnvironmentKind == EnvironmentKinds.Development, target.Event.Id, target.Event.Body, secret, ct);
        }

        sw.Stop();
        var now = clock.GetUtcNow();
        var exhausted = claim.AttemptNumber >= claim.MaxAttempts;
        string newStatus;
        DateTimeOffset nextAttempt = now;
        switch (result.Classification)
        {
            case AttemptClassification.Success:
                newStatus = DeliveryStatuses.Succeeded;
                break;
            case AttemptClassification.Retryable when !exhausted:
                newStatus = DeliveryStatuses.Pending;
                nextAttempt = now + _retry.NextDelay(claim.AttemptNumber + 1, Random.Shared.NextDouble(), result.RetryAfter);
                break;
            default:
                newStatus = DeliveryStatuses.Failed; // dead-lettered: visible via GET /v1/deliveries?status=failed, replayable
                break;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.CreateVersion7(started),
            OrganizationId = claim.OrganizationId,
            DeliveryId = claim.Id,
            AttemptNumber = claim.AttemptNumber,
            StartedAt = started,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Outcome = result.Classification switch
            {
                AttemptClassification.Success => AttemptOutcomes.Succeeded,
                AttemptClassification.Retryable => AttemptOutcomes.RetryableFailure,
                _ => AttemptOutcomes.PermanentFailure,
            },
            StatusCode = result.StatusCode,
            Error = result.Error is null ? null : Redaction.Text(result.Error),
            RequestBytes = result.RequestBytes,
            ResponseBytes = result.ResponseBytes,
        });

        var e = target.Event;
        db.UsageRecords.Add(Usage.Record(e.OrganizationId, e.ProjectId, e.EnvironmentId, UsageMetrics.DeliveryAttempts, 1, claim.Id, now));
        db.UsageRecords.Add(Usage.Record(e.OrganizationId, e.ProjectId, e.EnvironmentId, UsageMetrics.DeliveryBytesSent, result.RequestBytes, claim.Id, now));
        if (result.Classification == AttemptClassification.Success)
        {
            db.UsageRecords.Add(Usage.Record(e.OrganizationId, e.ProjectId, e.EnvironmentId, UsageMetrics.DeliveryBytesDelivered, result.RequestBytes, claim.Id, now));
        }

        await db.SaveChangesAsync(ct);

        // Only the lease holder may move the delivery on. If the lease was lost, the attempt is still recorded.
        var updated = await db.Deliveries
            .Where(d => d.Id == claim.Id && d.OrganizationId == claim.OrganizationId && d.LockedBy == workerId && d.Status == DeliveryStatuses.InFlight)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, newStatus)
                .SetProperty(d => d.NextAttemptAt, nextAttempt)
                .SetProperty(d => d.LastAttemptAt, started)
                .SetProperty(d => d.LastStatusCode, result.StatusCode)
                .SetProperty(d => d.LastError, result.Error == null ? null : Redaction.Text(result.Error, 300))
                .SetProperty(d => d.CompletedAt, newStatus == DeliveryStatuses.Pending ? null : now)
                .SetProperty(d => d.LockedBy, (string?)null)
                .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null), ct);
        await tx.CommitAsync(ct);

        if (updated == 0)
        {
            logger.LogWarning("Lease lost for delivery {DeliveryId} attempt {Attempt}; outcome recorded only", claim.Id, claim.AttemptNumber);
        }

        logger.LogInformation("Delivery {DeliveryId} attempt {Attempt}/{Max} to {Url}: {Outcome} {Status} in {Ms} ms",
            claim.Id, claim.AttemptNumber, claim.MaxAttempts, Redaction.Url(target.Destination.Url), result.Classification, result.StatusCode, sw.ElapsedMilliseconds);
    }

    private sealed record AttemptResult(AttemptClassification Classification, int? StatusCode, string? Error, int RequestBytes, int ResponseBytes, TimeSpan? RetryAfter);

    private async Task<AttemptResult> SendAsync(WebhookDestination destination, bool isDevelopmentEnvironment, Guid eventId, string body, string secret, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, destination.Url)
        {
            Content = new ByteArrayContent(bodyBytes),
            Version = new Version(1, 1),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation(WebhookSigner.IdHeader, eventId.ToString());
        request.Headers.TryAddWithoutValidation(WebhookSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSigner.SignatureHeader, WebhookSigner.Sign(secret, eventId.ToString(), timestamp, body));
        request.Headers.UserAgent.ParseAdd("RelayStack-Webhooks/0.1");
        request.Options.Set(OutboundPolicy.AllowDevelopmentReceiver, isDevelopmentEnvironment);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(destination.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var responseBytes = await ReadBoundedAsync(response, options.Delivery.MaxResponseBytesRead, timeout.Token);
            var classification = RetryPolicy.Classify(response.StatusCode);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var error = classification == AttemptClassification.Success ? null : $"HTTP {(int)response.StatusCode}";
            return new AttemptResult(classification, (int)response.StatusCode, error, bodyBytes.Length, responseBytes, retryAfter);
        }
        catch (Exception ex) when (FindBlocked(ex) is { } blocked)
        {
            return new AttemptResult(AttemptClassification.Permanent, null, "Blocked by outbound policy: " + blocked.Message, 0, 0, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AttemptResult(AttemptClassification.Retryable, null, $"Timed out after {destination.TimeoutSeconds} s.", bodyBytes.Length, 0, null);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptResult(AttemptClassification.Retryable, null, "Network error: " + ex.Message, bodyBytes.Length, 0, null);
        }
    }

    private static OutboundRequestBlockedException? FindBlocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OutboundRequestBlockedException b)
            {
                return b;
            }
        }

        return null;
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes of the response; the rest is discarded with the connection.</summary>
    private static async Task<int> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[Math.Min(limit, 4096)];
        var total = 0;
        int read;
        while (total < limit && (read = await s.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), ct)) > 0)
        {
            total += read;
        }

        return total;
    }
}
