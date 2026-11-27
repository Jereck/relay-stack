using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RelayStack.Platform.Metering;
using RelayStack.Platform.Persistence;

namespace RelayStack.Platform.Deliveries;

public sealed record EventDraft(
    Guid OrganizationId,
    Guid ProjectId,
    Guid EnvironmentId,
    string EnvironmentKind,
    string SourceKind,
    Guid SourceId,
    string DedupeKey,
    string Type,
    JsonObject Data,
    int RecordCount,
    DateTimeOffset ExpiresAt);

public sealed record DestinationTarget(Guid Id, int MaxAttempts);

public sealed record AppendResult(bool Inserted, int BodyBytes);

/// <summary>
/// Writes events and their deliveries in the caller's transaction: the transactional outbox.
/// An event and its delivery rows become visible together or not at all, and the delivery dispatcher only
/// ever reads committed delivery rows. Re-running the same unit of work hits the (source_id, dedupe_key)
/// unique constraint and is a no-op, which is what makes job re-execution safe.
/// </summary>
public static class EventWriter
{
    private static readonly JsonSerializerOptions BodyJson = new() { WriteIndented = false };

    public static string BuildBody(Guid eventId, string type, DateTimeOffset createdAt, string environmentKind, JsonObject data)
    {
        var envelope = new JsonObject
        {
            ["id"] = eventId.ToString(),
            ["type"] = type,
            ["created_at"] = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["environment"] = environmentKind,
            ["data"] = data,
        };
        return envelope.ToJsonString(BodyJson);
    }

    public static async Task<AppendResult> AppendAsync(RelayStackDbContext db, EventDraft draft, DestinationTarget? destination, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("EventWriter must be called inside a transaction.");
        }

        var eventId = Guid.CreateVersion7(now);
        var body = BuildBody(eventId, draft.Type, now, draft.EnvironmentKind, draft.Data);
        var bodyBytes = Encoding.UTF8.GetByteCount(body);

        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO events (id, organization_id, project_id, environment_id, source_kind, source_id, dedupe_key, type,
                                body, body_bytes, record_count, created_at, expires_at)
            VALUES ({eventId}, {draft.OrganizationId}, {draft.ProjectId}, {draft.EnvironmentId}, {draft.SourceKind}, {draft.SourceId},
                    {draft.DedupeKey}, {draft.Type}, {body}, {bodyBytes}, {draft.RecordCount}, {now}, {draft.ExpiresAt})
            ON CONFLICT (source_id, dedupe_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);

        if (inserted.Count == 0)
        {
            return new AppendResult(false, 0);
        }

        if (destination is not null)
        {
            db.Deliveries.Add(new Delivery
            {
                Id = Guid.CreateVersion7(now),
                OrganizationId = draft.OrganizationId,
                EventId = eventId,
                DestinationId = destination.Id,
                Status = DeliveryStatuses.Pending,
                MaxAttempts = destination.MaxAttempts,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }

        db.UsageRecords.Add(Usage.Record(draft.OrganizationId, draft.ProjectId, draft.EnvironmentId, UsageMetrics.EventsCreated, 1, eventId, now));
        db.UsageRecords.Add(Usage.Record(draft.OrganizationId, draft.ProjectId, draft.EnvironmentId, UsageMetrics.EventBytesCreated, bodyBytes, eventId, now));
        await db.SaveChangesAsync(ct);
        return new AppendResult(true, bodyBytes);
    }
}
