using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayStack.Platform;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Jobs;
using RelayStack.Platform.Persistence;
using RelayStack.Platform.Polling;
using RelayStack.Platform.Processing;
using RelayStack.Platform.Retention;
using RelayStack.Platform.Security;

namespace RelayStack.Worker;

public static class WorkerApp
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        var connectionString = builder.Configuration.GetConnectionString("Worker")
            ?? throw new InvalidOperationException("ConnectionStrings:Worker is required (the relaystack_worker role).");

        builder.Services.AddRelayStackObservability(builder.Logging, builder.Configuration, "relaystack-worker");
        builder.Services.AddRelayStackPlatform(options, DatabaseAccessMode.Worker, connectionString, builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(new WorkerIdentity($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}"));
        builder.Services.AddScoped<FileProcessor>();
        builder.Services.AddSingleton<RetentionSweeper>();
        builder.Services.AddSingleton(sp => SafeHttpClientFactory.Create(sp.GetRequiredService<OutboundPolicy>(), TimeSpan.FromSeconds(5)));
        builder.Services.AddSingleton(sp => new WorkerDbFactory(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton<DeliveryDispatcher>();
        builder.Services.AddSingleton<PollRunner>();
        builder.Services.AddHostedService<JobWorkerService>();
        builder.Services.AddHostedService<DeliveryWorkerService>();
        builder.Services.AddHostedService<PollingWorkerService>();
        builder.Services.AddHostedService<RetentionWorkerService>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return builder.Build();
    }
}

public sealed record WorkerIdentity(string Id);

/// <summary>Claims durable jobs and runs them with a timeout. Failures are retried with backoff; exhausted jobs fail the file.</summary>
public sealed class JobWorkerService(IServiceScopeFactory scopes, WorkerDbFactory dbFactory, PlatformOptions options, WorkerIdentity worker,
    TimeProvider clock, ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job worker {Worker} started", worker.Id);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken) == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job loop error");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<ClaimedJob> jobs;
        await using (var db = dbFactory.ForClaims())
        {
            jobs = await JobQueue.ClaimAsync(db, worker.Id, options.Processing.MaxConcurrentJobs, TimeSpan.FromSeconds(options.Processing.JobLeaseSeconds), clock.GetUtcNow(), ct);
        }

        await Task.WhenAll(jobs.Select(j => RunJobAsync(j, ct)));
        return jobs.Count;
    }

    private async Task RunJobAsync(ClaimedJob job, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Processing.JobTimeoutSeconds));
        try
        {
            using var scope = scopes.CreateScope();
            // Everything the handler reads or writes through the scoped context is limited to the job's organization.
            scope.ServiceProvider.GetRequiredService<RelayStackDbContext>().ScopeToOrganization(job.OrganizationId);
            switch (job.Kind)
            {
                case Platform.JobKinds.ProcessFile:
                    await scope.ServiceProvider.GetRequiredService<FileProcessor>().ProcessAsync(job.SubjectId, timeout.Token);
                    break;
                default:
                    throw new PermanentProcessingException("unknown_job_kind", $"Unknown job kind {job.Kind}.");
            }

            await using var db = dbFactory.For(job.OrganizationId);
            await JobQueue.CompleteAsync(db, job.Id, worker.Id, clock.GetUtcNow(), CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave the job leased; it becomes claimable when the lease expires.
            logger.LogInformation("Job {JobId} interrupted by shutdown; it will be retried after its lease expires", job.Id);
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? $"Timed out after {options.Processing.JobTimeoutSeconds} s." : ex.GetType().Name + ": " + Redaction.Text(ex.Message, 300);
            await using var db = dbFactory.For(job.OrganizationId);
            var exhausted = await JobQueue.FailAsync(db, job, worker.Id, reason, permanent: ex is PermanentProcessingException,
                TimeSpan.FromSeconds(30 * job.Attempts), clock.GetUtcNow(), CancellationToken.None);
            logger.LogError(ex, "Job {JobId} attempt {Attempt}/{Max} failed (exhausted: {Exhausted})", job.Id, job.Attempts, job.MaxAttempts, exhausted);
            if (exhausted && job.Kind == Platform.JobKinds.ProcessFile)
            {
                await db.Files.Where(f => f.Id == job.SubjectId && f.Status != FileStatuses.Completed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Status, FileStatuses.Failed)
                        .SetProperty(f => f.ErrorCode, "processing_failed")
                        .SetProperty(f => f.ErrorMessage, reason)
                        .SetProperty(f => f.CompletedAt, clock.GetUtcNow()), CancellationToken.None);
            }
        }
    }
}

public sealed class DeliveryWorkerService(DeliveryDispatcher dispatcher, WorkerIdentity worker, ILogger<DeliveryWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Delivery worker {Worker} started", worker.Id);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await dispatcher.RunOnceAsync(worker.Id, stoppingToken) == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery loop error");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

public sealed class PollingWorkerService(PollRunner runner, WorkerIdentity worker, ILogger<PollingWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await runner.RunDueAsync(worker.Id, stoppingToken) == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

public sealed class RetentionWorkerService(RetentionSweeper sweeper, WorkerDbFactory dbFactory, TimeProvider clock, ILogger<RetentionWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sweeper.RunOnceAsync(dbFactory, clock.GetUtcNow(), 500, stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention sweep failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
