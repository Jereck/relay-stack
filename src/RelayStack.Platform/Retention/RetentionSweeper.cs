using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Storage;

namespace RelayStack.Platform.Retention;

/// <summary>
/// Enforces payload retention: deletes original files and purges event bodies once they expire.
/// After purge, event metadata, delivery history and usage remain; replay is no longer possible.
/// Organizations with expired data are found through a SECURITY DEFINER function; each one is then swept in a
/// context scoped to that organization (ADR-014).
/// </summary>
public sealed class RetentionSweeper(IObjectStore store, ILogger<RetentionSweeper> logger)
{
    public async Task<(int Files, int Events)> RunOnceAsync(WorkerDbFactory dbFactory, DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        List<Guid> organizations;
        await using (var claims = dbFactory.ForClaims())
        {
            organizations = await claims.Database
                .SqlQuery<Guid>($"SELECT o AS \"Value\" FROM worker_orgs_with_expired_payloads({now}, {100}) AS o")
                .ToListAsync(ct);
        }

        int files = 0, events = 0;
        foreach (var organizationId in organizations)
        {
            await using var db = dbFactory.For(organizationId);
            var (f, e) = await SweepOrganizationAsync(db, now, batchSize, ct);
            files += f;
            events += e;
        }

        if (files > 0 || events > 0)
        {
            logger.LogInformation("Retention: expired {Files} files and purged {Events} event payloads across {Organizations} organizations",
                files, events, organizations.Count);
        }

        return (files, events);
    }

    private async Task<(int Files, int Events)> SweepOrganizationAsync(RelayStackDbContext db, DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var expiredFiles = await db.Files
            .Where(f => f.ExpiresAt <= now && f.Status != FileStatuses.Expired && f.Status != FileStatuses.Received && f.Status != FileStatuses.Processing)
            .OrderBy(f => f.ExpiresAt).Take(batchSize).ToListAsync(ct);
        foreach (var f in expiredFiles)
        {
            // Delete the object first: if we crash afterwards the row is retried and deletion is idempotent.
            await store.DeleteAsync(TenantObjectKey.Parse(f.OrganizationId, f.StorageKey), ct);
            f.Status = FileStatuses.Expired;
        }

        await db.SaveChangesAsync(ct);

        var purged = await db.Events
            .Where(e => e.ExpiresAt <= now && e.PurgedAt == null)
            .OrderBy(e => e.ExpiresAt)
            .Take(batchSize)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Body, (string?)null).SetProperty(e => e.PurgedAt, now), ct);

        return (expiredFiles.Count, purged);
    }
}
