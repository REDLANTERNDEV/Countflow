using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
namespace Countflow.Modules.Work.Features;

public sealed record DailyThroughput(DateOnly Date, int Completed);
public sealed record KindThroughput(WorkTaskKind Kind, int Completed);
public sealed record WorkThroughputResponse(DateTimeOffset From, DateTimeOffset To, DateTimeOffset ObservedAt,
    int Completed, int Ready, int Assigned, IReadOnlyList<DailyThroughput> Days, IReadOnlyList<KindThroughput> ByKind);
public static class GetWorkThroughput
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/work/throughput", async (Guid warehouseId, int? days, WorkDbContext db, IWarehouseDirectory warehouses,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var length = days ?? 7;
            if (length is < 1 or > 90) return Result.Failure(Error.Validation("work.throughput_window_invalid", "Choose 1–90 days.")).ToHttpResult();
            if (await warehouses.FindAsync(warehouseId, cancellationToken) is not { IsActive: true }) return Results.NotFound();
            var until = clock.GetUtcNow(); var first = until.UtcDateTime.Date.AddDays(1 - length); var since = new DateTimeOffset(first, TimeSpan.Zero);
            // Npgsql translates DateTimeOffset.DateTime to AT TIME ZONE UTC.
            // UtcDateTime is only a CLR conversion and mis-maps this grouped key.
            var rows = await db.WorkTasks.AsNoTracking().Where(task => task.WarehouseId == warehouseId && task.Status == WorkTaskStatus.Completed
                && task.CompletedAt >= since && task.CompletedAt <= until)
                .GroupBy(task => new { Date = task.CompletedAt!.Value.DateTime.Date, task.Kind })
                .Select(group => new CompletionRow(group.Key.Date, group.Key.Kind, group.Count())).ToListAsync(cancellationToken);
            var backlog = await db.WorkTasks.AsNoTracking().Where(task => task.WarehouseId == warehouseId
                && (task.Status == WorkTaskStatus.Ready || task.Status == WorkTaskStatus.Assigned))
                .GroupBy(task => task.Status).Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
            var daily = rows.GroupBy(row => DateOnly.FromDateTime(row.Date)).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
            return Results.Ok(new WorkThroughputResponse(since, until, until, rows.Sum(row => row.Count),
                backlog.Where(row => row.Status == WorkTaskStatus.Ready).Sum(row => row.Count), backlog.Where(row => row.Status == WorkTaskStatus.Assigned).Sum(row => row.Count),
                Enumerable.Range(0, length).Select(offset => { var day = DateOnly.FromDateTime(first.AddDays(offset)); return new DailyThroughput(day, daily.GetValueOrDefault(day)); }).ToList(),
                rows.GroupBy(row => row.Kind).OrderBy(group => group.Key).Select(group => new KindThroughput(group.Key, group.Sum(row => row.Count))).ToList()));
        }).RequireAuthorization().WithTags("Work").WithName("GetWorkThroughput")
            .WithSummary("Completed work by UTC completion date and the current backlog.").Produces<WorkThroughputResponse>().ProducesProblem(400).ProducesProblem(404);
    }
    private sealed record CompletionRow(DateTime Date, WorkTaskKind Kind, int Count);
}
