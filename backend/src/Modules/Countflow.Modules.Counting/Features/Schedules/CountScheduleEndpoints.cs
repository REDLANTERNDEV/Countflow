using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Counting.Features.Plans;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Counting.Features.Schedules;

public sealed record SaveCountScheduleRequest(bool Enabled, int IntervalMinutes = 1440, int MaxTasks = 20);
public sealed record CountScheduleResponse(Guid WarehouseId, string WarehouseCode, bool Enabled,
    int IntervalMinutes, int MaxTasks, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
    Guid? LastPlanId, bool IsPending, string? LastErrorCode);

public static class CountScheduleEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/counting/schedules", async (CountingDbContext db, IWarehouseDirectory warehouses,
            CancellationToken cancellationToken) =>
        {
            var schedules = await db.CountSchedules.AsNoTracking().ToDictionaryAsync(row => row.WarehouseId, cancellationToken);
            return Results.Ok((await warehouses.ListAsync(cancellationToken)).Where(warehouse => warehouse.IsActive)
                .Select(warehouse => Describe(warehouse, schedules.GetValueOrDefault(warehouse.Id))).ToList());
        }).RequireAuthorization().WithTags("Counting").WithName("GetCountSchedules")
            .WithSummary("List warehouse cycle-count schedules.").Produces<List<CountScheduleResponse>>();

        endpoints.MapPut("/counting/schedules/{warehouseId:guid}", async (Guid warehouseId,
            SaveCountScheduleRequest request, CountingDbContext db, IWarehouseDirectory warehouses,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (request.MaxTasks is < 1 or > CountPlanning.MaxTasksPerPlan)
                return Result.Failure(CountingErrors.ScheduleBudgetInvalid).ToHttpResult();
            if (request.IntervalMinutes is < 1 or > 10080)
                return Result.Failure(CountingErrors.ScheduleIntervalInvalid).ToHttpResult();
            var warehouse = await warehouses.FindAsync(warehouseId, cancellationToken);
            if (warehouse is not { IsActive: true }) return Result.Failure(CountingErrors.WarehouseNotFound).ToHttpResult();
            await using var lease = await CountPlanningLock.AcquireAsync(db, warehouseId, cancellationToken);
            var schedule = await db.CountSchedules.SingleOrDefaultAsync(row => row.WarehouseId == warehouseId, cancellationToken);
            if (schedule is null)
            {
                schedule = new CountSchedule { WarehouseId = warehouseId };
                db.CountSchedules.Add(schedule);
            }
            var changed = schedule.Enabled != request.Enabled || schedule.IntervalMinutes != request.IntervalMinutes;
            schedule.Enabled = request.Enabled;
            schedule.IntervalMinutes = request.IntervalMinutes;
            schedule.MaxTasks = request.MaxTasks;
            schedule.Revision = Guid.CreateVersion7();
            if (schedule.PendingRunId is null && (changed || schedule.NextRunAt is null))
                schedule.NextRunAt = schedule.Enabled ? clock.GetUtcNow().AddMinutes(schedule.IntervalMinutes) : null;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(Describe(warehouse, schedule));
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Counting").WithName("SaveCountSchedule")
            .WithSummary("Save an opt-in cycle-count schedule.").Produces<CountScheduleResponse>()
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);
    }

    private static CountScheduleResponse Describe(WarehouseSnapshot warehouse, CountSchedule? schedule) =>
        new(warehouse.Id, warehouse.Code, schedule?.Enabled ?? false, schedule?.IntervalMinutes ?? 1440,
            schedule?.MaxTasks ?? 20, schedule?.NextRunAt, schedule?.LastRunAt, schedule?.LastPlanId,
            schedule?.PendingRunId is not null, schedule?.LastErrorCode);
}
