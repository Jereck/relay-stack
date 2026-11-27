using Microsoft.EntityFrameworkCore;
using RelayStack.Platform.Persistence;

namespace RelayStack.Platform.Jobs;

public sealed record ClaimedJob(Guid Id, Guid OrganizationId, string Kind, Guid SubjectId, int Attempts, int MaxAttempts);

/// <summary>
/// Durable job queue on Postgres (ADR-004). Jobs are claimed with <c>FOR UPDATE SKIP LOCKED</c> and a lease.
/// A worker that crashes simply stops renewing nothing: when <c>locked_until</c> passes, the job becomes
/// claimable again. Accepted work is therefore never silently lost, and handlers must be idempotent.
/// </summary>
public static class JobQueue
{
    public static Job NewProcessFileJob(Guid organizationId, Guid fileId, int maxAttempts, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = organizationId,
        Kind = JobKinds.ProcessFile,
        SubjectId = fileId,
        Status = JobStatuses.Pending,
        MaxAttempts = maxAttempts,
        RunAfter = now,
        CreatedAt = now,
    };

    public static async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(RelayStackDbContext db, string workerId, int max, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
    {
        var leaseUntil = now + lease;
        // Cross-tenant by design: worker_claim_jobs (SECURITY DEFINER) leases due rows and returns only those (ADR-014).
        var rows = await db.Jobs
            .FromSql($"SELECT * FROM worker_claim_jobs({workerId}, {max}, {leaseUntil}, {now})")
            .AsNoTracking().IgnoreQueryFilters().ToListAsync(ct);

        return rows.Select(j => new ClaimedJob(j.Id, j.OrganizationId, j.Kind, j.SubjectId, j.Attempts, j.MaxAttempts)).ToList();
    }

    /// <summary>Marks a job finished. Returns false if this worker no longer holds the lease. <paramref name="db"/> must be scoped to the job's organization.</summary>
    public static async Task<bool> CompleteAsync(RelayStackDbContext db, Guid jobId, string workerId, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.Jobs.IgnoreQueryFilters()
            .Where(j => j.Id == jobId && j.LockedBy == workerId && j.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatuses.Succeeded)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null), ct);
        return n == 1;
    }

    /// <summary>Schedules a retry, or fails the job permanently when attempts are exhausted. Returns true if failed permanently.</summary>
    public static async Task<bool> FailAsync(RelayStackDbContext db, ClaimedJob job, string workerId, string error, bool permanent, TimeSpan retryDelay, DateTimeOffset now, CancellationToken ct)
    {
        var exhausted = permanent || job.Attempts >= job.MaxAttempts;
        await db.Jobs.IgnoreQueryFilters()
            .Where(j => j.Id == job.Id && j.LockedBy == workerId && j.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, exhausted ? JobStatuses.Failed : JobStatuses.Pending)
                .SetProperty(j => j.RunAfter, now + retryDelay)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.CompletedAt, exhausted ? now : null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null), ct);
        return exhausted;
    }
}
