using Countflow.Modules.Jobs.Domain;
using Countflow.Modules.Jobs.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Countflow.Modules.Jobs;

/// <summary>
/// Runs due, code-registered jobs. Scheduling and unfinished-run identities survive
/// restarts. A dedicated PostgreSQL session lock excludes healthy rival replicas;
/// after a crash the same run is retried, so handlers must use its idempotency key.
/// </summary>
public sealed class BackgroundJobRunner(
    IServiceScopeFactory scopes,
    IEnumerable<BackgroundJobRegistration> jobs,
    IOptionsMonitor<RecurringJobOptions> schedules,
    TimeProvider clock,
    ILogger<BackgroundJobRunner> logger)
{
    public Task RunDueAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(jobs.Select(job => RunJobAsync(job, cancellationToken)));

    public async Task RunJobAsync(BackgroundJobRegistration job, CancellationToken cancellationToken)
    {
        var settings = job.Schedule ?? schedules.Get(job.Name);
        if (!settings.Enabled) return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            // Never return a session holding advisory locks to a connection pool.
            var connectionString = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false, ApplicationName = $"countflow.job:{job.Name}" };
            await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(hashtextextended(@name, 2026))";
            command.Parameters.AddWithValue("name", $"countflow.job:{job.Name}");
            if (await command.ExecuteScalarAsync(cancellationToken) is not true) return;

            var state = await db.JobStates.SingleOrDefaultAsync(jobState => jobState.Name == job.Name, cancellationToken);
            if (state is null)
            {
                state = new JobState { Name = job.Name, NextRunAt = clock.GetUtcNow() + settings.InitialDelay };
                db.JobStates.Add(state);
                await db.SaveChangesAsync(cancellationToken);
            }
            if (state.NextRunAt > clock.GetUtcNow()) return;

            state.PendingRunId ??= Guid.CreateVersion7();
            state.PendingScheduledAt ??= state.NextRunAt;
            // Fence completion/failure saves if a replica replaces this attempt
            // after its separate advisory-lock connection is lost.
            state.AttemptId = Guid.CreateVersion7();
            state.LastStartedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var run = new BackgroundJobRun(job.Name, state.PendingRunId.Value, state.PendingScheduledAt!.Value);

            logger.LogInformation("Background job {Job} run {RunId} started (scheduled {ScheduledAt})", job.Name, run.Id, run.ScheduledAt);
            try
            {
                var handler = (IBackgroundJob)scope.ServiceProvider.GetRequiredService(job.HandlerType);
                await handler.ExecuteAsync(run, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                state.LastRunId = run.Id;
                state.LastFinishedAt = clock.GetUtcNow();
                state.ConsecutiveFailures++;
                state.LastErrorCode = "jobs.execution_failed";
                var backoff = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(state.ConsecutiveFailures - 1, 4))));
                state.NextRunAt = clock.GetUtcNow() + backoff;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogError(exception, "Background job {Job} run {RunId} failed; retry due {NextRunAt}", job.Name, run.Id, state.NextRunAt);
                return;
            }
            state.LastRunId = run.Id;
            state.LastFinishedAt = clock.GetUtcNow();
            state.PendingRunId = null;
            state.PendingScheduledAt = null;
            state.ConsecutiveFailures = 0;
            state.LastErrorCode = null;
            // No burst of catch-up runs after downtime; resume the cadence from
            // completion. An unfinished run remains due and retains its key.
            state.NextRunAt = clock.GetUtcNow() + settings.Interval;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Background job {Job} run {RunId} finished; next due {NextRunAt}", job.Name, run.Id, state.NextRunAt);

        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A database outage must not kill the process or unrelated jobs. Any
            // saved pending identity is recovered on the next poll or replica.
            logger.LogError(exception, "Background job {Job} could not be dispatched", job.Name);
        }
    }
}
