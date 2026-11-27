using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayStack.Platform.Persistence;

namespace RelayStack.Platform.Deliveries;

public enum ReplayOutcome
{
    Created,
    AlreadyActive,
    NotFound,
    NotTerminal,
    Expired,
    DestinationDisabled,
}

public sealed record ReplayResult(ReplayOutcome Outcome, Delivery? Delivery);

/// <summary>
/// Manual replay creates a NEW delivery for the same event (same webhook-id, so receivers can dedupe).
/// At most one replay of a delivery can be active at a time, enforced by a unique index, which makes
/// repeated replay requests idempotent while the replay is pending or in flight.
/// </summary>
public static class ReplayService
{
    public static async Task<ReplayResult> ReplayAsync(RelayStackDbContext db, Guid deliveryId, DateTimeOffset now, CancellationToken ct)
    {
        var original = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (original is null)
        {
            return new ReplayResult(ReplayOutcome.NotFound, null);
        }

        if (original.Status is not (DeliveryStatuses.Failed or DeliveryStatuses.Succeeded or DeliveryStatuses.Cancelled))
        {
            return new ReplayResult(ReplayOutcome.NotTerminal, original);
        }

        var active = await FindActiveReplayAsync(db, original.Id, ct);
        if (active is not null)
        {
            return new ReplayResult(ReplayOutcome.AlreadyActive, active);
        }

        var ev = await db.Events.SingleAsync(e => e.Id == original.EventId, ct);
        if (ev.Body is null || ev.ExpiresAt <= now)
        {
            return new ReplayResult(ReplayOutcome.Expired, original);
        }

        var destination = await db.Destinations.SingleAsync(d => d.Id == original.DestinationId, ct);
        if (!destination.Enabled)
        {
            return new ReplayResult(ReplayOutcome.DestinationDisabled, original);
        }

        var replay = new Delivery
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = original.OrganizationId,
            EventId = original.EventId,
            DestinationId = original.DestinationId,
            Status = DeliveryStatuses.Pending,
            MaxAttempts = destination.MaxAttempts,
            NextAttemptAt = now,
            ReplayOf = original.Id,
            CreatedAt = now,
        };
        db.Deliveries.Add(replay);
        try
        {
            await db.SaveChangesAsync(ct);
            return new ReplayResult(ReplayOutcome.Created, replay);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return new ReplayResult(ReplayOutcome.AlreadyActive, await FindActiveReplayAsync(db, original.Id, ct));
        }
    }

    private static Task<Delivery?> FindActiveReplayAsync(RelayStackDbContext db, Guid originalId, CancellationToken ct) =>
        db.Deliveries.Where(d => d.ReplayOf == originalId && (d.Status == DeliveryStatuses.Pending || d.Status == DeliveryStatuses.InFlight))
            .SingleOrDefaultAsync(ct);
}
