using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelayStack.Api.Auth;
using RelayStack.Platform;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Security;
using RelayStack.Platform.Tenancy;

namespace RelayStack.Api.Endpoints;

internal static class DeliveryEndpoints
{
    public static void MapDeliveryEndpoints(this RouteGroupBuilder v1)
    {
        var events = v1.MapGroup("/events").WithTags("Events");
        events.MapGet("", ListEvents).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.EventsRead)).WithName("ListEvents")
            .WithSummary("Events generated in this environment, newest first. Filter by source_id (file or poll source) or type.");
        events.MapGet("/{id:guid}", GetEvent).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.EventsRead)).WithName("GetEvent")
            .WithSummary("An event including its exact JSON payload, until the payload is purged at the end of retention.");
        events.MapGet("/{id:guid}/deliveries", ListEventDeliveries).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DeliveriesRead)).WithName("ListEventDeliveries");

        var deliveries = v1.MapGroup("/deliveries").WithTags("Deliveries");
        deliveries.MapGet("", ListDeliveries).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DeliveriesRead)).WithName("ListDeliveries")
            .WithSummary("Deliveries newest first. status=failed lists dead-lettered deliveries.");
        deliveries.MapGet("/{id:guid}", GetDelivery).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DeliveriesRead)).WithName("GetDelivery");
        deliveries.MapGet("/{id:guid}/attempts", ListAttempts).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DeliveriesRead)).WithName("ListDeliveryAttempts");
        deliveries.MapPost("/{id:guid}/replay", Replay).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.DeliveriesReplay)).WithName("ReplayDelivery")
            .WithSummary("Re-send a failed or succeeded delivery as a new delivery with the same event id. Idempotent while the replay is active.");

        v1.MapGet("/usage", GetUsage).RequireAuthorization(ApiKeyDefaults.Policy(Scopes.UsageRead)).WithTags("Usage").WithName("GetUsage")
            .WithSummary("Metered usage totals for this environment and derived amplification ratios.");
    }

    private static async Task<IResult> ListEvents(RelayStackDbContext db, [FromQuery(Name = "source_id")] Guid? sourceId, string? type, int? limit, [FromQuery(Name = "starting_after")] Guid? startingAfter, CancellationToken ct)
    {
        var n = Paging.Clamp(limit);
        var q = db.Events.AsNoTracking();
        if (sourceId is { } src)
        {
            q = q.Where(e => e.SourceId == src);
        }

        if (!string.IsNullOrEmpty(type))
        {
            q = q.Where(e => e.Type == type);
        }

        if (startingAfter is { } after)
        {
            q = q.Where(e => e.Id.CompareTo(after) < 0);
        }

        // Project away the body: list responses never carry payloads.
        var rows = await q.OrderByDescending(e => e.Id).Take(n + 1)
            .Select(e => new EventRecord
            {
                Id = e.Id, OrganizationId = e.OrganizationId, ProjectId = e.ProjectId, EnvironmentId = e.EnvironmentId, SourceKind = e.SourceKind,
                SourceId = e.SourceId, DedupeKey = e.DedupeKey, Type = e.Type, Body = e.Body == null ? null : "", BodyBytes = e.BodyBytes,
                RecordCount = e.RecordCount, CreatedAt = e.CreatedAt, ExpiresAt = e.ExpiresAt, PurgedAt = e.PurgedAt,
            })
            .ToListAsync(ct);
        return Results.Ok(Paging.Page(rows, n, e => e.Id, e => EventResponse.From(e, includePayload: false)));
    }

    private static async Task<IResult> GetEvent(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct) is { } e
            ? Results.Ok(EventResponse.From(e, includePayload: true))
            : ApiErrors.NotFound("Event");

    /// <summary>Deliveries are visible only through an event in the caller's environment.</summary>
    private static IQueryable<Delivery> VisibleDeliveries(RelayStackDbContext db) =>
        from d in db.Deliveries
        join e in db.Events on d.EventId equals e.Id
        select d;

    private static async Task<IResult> ListEventDeliveries(Guid id, RelayStackDbContext db, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, ct))
        {
            return ApiErrors.NotFound("Event");
        }

        var rows = await VisibleDeliveries(db).AsNoTracking().Where(d => d.EventId == id).OrderBy(d => d.Id).Take(100).ToListAsync(ct);
        return Results.Ok(new ListResponse<DeliveryResponse>(rows.Select(DeliveryResponse.From).ToList(), false, null));
    }

    private static async Task<IResult> ListDeliveries(RelayStackDbContext db, string? status, [FromQuery(Name = "event_id")] Guid? eventId, int? limit, [FromQuery(Name = "starting_after")] Guid? startingAfter, CancellationToken ct)
    {
        if (status is not null && status is not (DeliveryStatuses.Pending or DeliveryStatuses.InFlight or DeliveryStatuses.Succeeded or DeliveryStatuses.Failed or DeliveryStatuses.Cancelled))
        {
            return ApiErrors.Validation("status must be one of pending, in_flight, succeeded, failed, cancelled.");
        }

        var n = Paging.Clamp(limit);
        var q = VisibleDeliveries(db).AsNoTracking();
        if (status is not null)
        {
            q = q.Where(d => d.Status == status);
        }

        if (eventId is { } ev)
        {
            q = q.Where(d => d.EventId == ev);
        }

        if (startingAfter is { } after)
        {
            q = q.Where(d => d.Id.CompareTo(after) < 0);
        }

        var rows = await q.OrderByDescending(d => d.Id).Take(n + 1).ToListAsync(ct);
        return Results.Ok(Paging.Page(rows, n, d => d.Id, DeliveryResponse.From));
    }

    private static async Task<IResult> GetDelivery(Guid id, RelayStackDbContext db, CancellationToken ct) =>
        await VisibleDeliveries(db).AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } d
            ? Results.Ok(DeliveryResponse.From(d))
            : ApiErrors.NotFound("Delivery");

    private static async Task<IResult> ListAttempts(Guid id, RelayStackDbContext db, CancellationToken ct)
    {
        if (!await VisibleDeliveries(db).AnyAsync(d => d.Id == id, ct))
        {
            return ApiErrors.NotFound("Delivery");
        }

        var rows = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.AttemptNumber).ToListAsync(ct);
        return Results.Ok(new ListResponse<AttemptResponse>(rows.Select(AttemptResponse.From).ToList(), false, null));
    }

    private static async Task<IResult> Replay(Guid id, RelayStackDbContext db, TenantContext tenant, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        if (!await VisibleDeliveries(db).AnyAsync(d => d.Id == id, ct))
        {
            return ApiErrors.NotFound("Delivery");
        }

        var now = clock.GetUtcNow();
        var result = await ReplayService.ReplayAsync(db, id, now, ct);
        switch (result.Outcome)
        {
            case ReplayOutcome.Created:
                Audit.Write(db, tenant, http, "delivery.replayed", "delivery", id.ToString(), new { replay_id = result.Delivery!.Id }, now);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/v1/deliveries/{result.Delivery.Id}", DeliveryResponse.From(result.Delivery));
            case ReplayOutcome.AlreadyActive:
                return Results.Ok(DeliveryResponse.From(result.Delivery!));
            case ReplayOutcome.NotTerminal:
                return ApiErrors.Problem(409, "delivery_in_progress", "Only failed, succeeded or cancelled deliveries can be replayed.");
            case ReplayOutcome.Expired:
                return ApiErrors.Problem(410, "replay_window_expired", "The event payload is past its retention period and can no longer be replayed.");
            case ReplayOutcome.DestinationDisabled:
                return ApiErrors.Problem(409, "destination_disabled", "The destination is disabled.");
            default:
                return ApiErrors.NotFound("Delivery");
        }
    }

    private static async Task<IResult> GetUsage(RelayStackDbContext db, DateTimeOffset? from, DateTimeOffset? to, TimeProvider clock, CancellationToken ct)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-30);
        if (start > end || end - start > TimeSpan.FromDays(366))
        {
            return ApiErrors.Validation("from must be before to and the range at most 366 days.");
        }

        var rows = await db.UsageRecords.AsNoTracking()
            .Where(u => u.OccurredAt >= start && u.OccurredAt < end)
            .GroupBy(u => u.Metric)
            .Select(g => new { Metric = g.Key, Total = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var totals = UsageMetrics.All.ToDictionary(m => m, m => rows.FirstOrDefault(r => r.Metric == m)?.Total ?? 0L);

        static double? Ratio(long a, long b) => b == 0 ? null : Math.Round((double)a / b, 4);
        var ratios = new UsageRatios(
            Ratio(totals[UsageMetrics.DeliveryBytesSent], totals[UsageMetrics.BytesIngested]),
            Ratio(totals[UsageMetrics.DeliveryAttempts], totals[UsageMetrics.EventsCreated]),
            Ratio(totals[UsageMetrics.EventBytesCreated], totals[UsageMetrics.BytesIngested]),
            Ratio(totals[UsageMetrics.DeliveryBytesSent] - totals[UsageMetrics.DeliveryBytesDelivered], totals[UsageMetrics.DeliveryBytesDelivered]));
        return Results.Ok(new UsageResponse(start, end, totals, ratios));
    }
}
