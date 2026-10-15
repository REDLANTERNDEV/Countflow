using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Counting.Features.Plans;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Counting.Features.Schedules;

/// <summary>Each configured warehouse is an independent instance of this code handler.</summary>
public sealed class ScheduledCountPlanning(CountScheduleRunner runner) : IBackgroundJob
{
    public Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken) =>
        runner.RunAsync(Guid.ParseExact(run.Name["counting.".Length..], "N"), cancellationToken);
}

public sealed class CountScheduleCatalog(CountingDbContext db) : IBackgroundJobCatalog
{
    public async Task<IReadOnlyList<Countflow.SharedKernel.Modules.BackgroundJobRegistration>> ListAsync(CancellationToken cancellationToken)
    {
        var ids = await db.CountSchedules.AsNoTracking()
            .Where(schedule => schedule.Enabled || schedule.PendingRunId != null)
            .Select(schedule => schedule.WarehouseId).ToListAsync(cancellationToken);
        return ids.Select(id => new Countflow.SharedKernel.Modules.BackgroundJobRegistration($"counting.{id:N}",
            typeof(ScheduledCountPlanning), new RecurringJobOptions
            { Enabled = true, Interval = TimeSpan.FromMinutes(1), InitialDelay = TimeSpan.Zero })).ToList();
    }
}

public sealed class CountScheduleRunner(CountingDbContext db, CountPlanning planning,
    IWarehouseDirectory warehouses, TimeProvider clock, ILogger<CountScheduleRunner> logger)
{
    public async Task RunAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        await using var lease = await CountPlanningLock.AcquireAsync(db, warehouseId, cancellationToken);
        var schedule = await db.CountSchedules.SingleAsync(schedule => schedule.WarehouseId == warehouseId, cancellationToken);
        if (schedule.PendingRunId is null && (!schedule.Enabled || schedule.NextRunAt > clock.GetUtcNow())) return;
        schedule.PendingRunId ??= Guid.CreateVersion7();
        schedule.PendingScheduledAt ??= schedule.NextRunAt ?? clock.GetUtcNow();
        schedule.Revision = Guid.CreateVersion7();
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var warehouse = await warehouses.FindAsync(warehouseId, cancellationToken);
            if (warehouse is not { IsActive: true }) throw new InvalidOperationException("counting.schedule_warehouse_unavailable");
            var key = $"schedule:{warehouseId:N}:{schedule.PendingRunId:N}";
            var result = await planning.PlanCoreAsync(warehouseId, schedule.MaxTasks, null, key,
                new PostingActor(Guid.Empty, "count-scheduler"), cancellationToken);
            if (result.IsFailure) throw new InvalidOperationException(result.Error.Code);
            if (result.Value.Status == CountPlanStatus.Open && result.Value.Lines.Any(line => line.WorkTaskId is null))
                throw new InvalidOperationException("counting.schedule_incomplete");
            schedule.LastPlanId = result.Value.Id;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            schedule.LastErrorCode = "counting.schedule_failed";
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Count schedule for warehouse {WarehouseId} failed", warehouseId);
            throw;
        }
        schedule.PendingRunId = null;
        schedule.PendingScheduledAt = null;
        schedule.LastRunAt = clock.GetUtcNow();
        schedule.LastErrorCode = null;
        schedule.NextRunAt = schedule.Enabled ? clock.GetUtcNow().AddMinutes(schedule.IntervalMinutes) : null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
