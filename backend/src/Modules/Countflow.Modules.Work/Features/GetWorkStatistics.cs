using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Work.Features;

/// <param name="Overridden">
/// Completed somewhere other than the rules suggested. Never counted for
/// <see cref="WorkTaskKind.CycleCount"/> — a count has no destination to disagree
/// about — so the rate for that kind is structurally zero rather than
/// suspiciously good.
/// </param>
public sealed record WorkKindStatistics(
    WorkTaskKind Kind,
    int Completed,
    int Overridden,
    int Cancelled,
    int Open)
{
    /// <summary>
    /// Overrides over completions. Null rather than zero when nothing has been
    /// completed, for the same reason accuracy is null on an uncounted zone: an
    /// unmeasured rate reported as perfect is worse than no rate.
    /// </summary>
    public decimal? OverrideRate =>
        Completed == 0 ? null : Math.Round((decimal)Overridden / Completed, 4);
}

public sealed record WorkStatisticsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<WorkKindStatistics> ByKind);

/// <summary>
/// How often the floor disagreed with the rules.
/// <para>
/// The number ADR 0004 is ultimately judged on, and the reason an override is
/// recorded rather than refused. A rule set nobody overrides is either very good or
/// never questioned; a rule set overridden a third of the time is telling its
/// author something, and the whole argument for directed work rests on somebody
/// being able to see which.
/// </para>
/// <para>
/// Work's own slice of the accuracy screen, served separately from Inventory's
/// counts and drift rather than composed server-side. A dashboard endpoint that
/// joined all three would have to live in some module and reach into the other two,
/// which is precisely the coupling ADR 0002 forbids — so each module answers for
/// what it owns and the console puts the three answers side by side. That is the
/// honest cost of the boundary, and three parallel fetches is a cheap price.
/// </para>
/// </summary>
public static class GetWorkStatistics
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/work/statistics", async (
                Guid warehouseId,
                DateTimeOffset? from,
                DateTimeOffset? to,
                WorkDbContext dbContext,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var now = clock.GetUtcNow();
                var since = from ?? now.AddDays(-90);
                var until = to ?? now;

                // Grouped in the database. Unlike the count accuracy figures, this
                // needs nothing from another module to group by — a task's kind is
                // Work's own fact — so there is no reason to pull the rows back.
                var rows = await dbContext.WorkTasks
                    .AsNoTracking()
                    .Where(t => t.WarehouseId == warehouseId && t.CreatedAt >= since && t.CreatedAt <= until)
                    .GroupBy(t => t.Kind)
                    .Select(group => new WorkKindStatistics(
                        group.Key,
                        group.Count(t => t.Status == WorkTaskStatus.Completed),
                        group.Count(t => t.Status == WorkTaskStatus.Completed && t.WasOverridden),
                        group.Count(t => t.Status == WorkTaskStatus.Cancelled),
                        group.Count(t => t.Status == WorkTaskStatus.Ready || t.Status == WorkTaskStatus.Assigned)))
                    .ToListAsync(cancellationToken);

                return Results.Ok(new WorkStatisticsResponse(
                    since,
                    until,
                    [.. rows.OrderBy(row => row.Kind)]));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("GetWorkStatistics")
            .WithSummary("Completion and override rates by task kind over a window.")
            .Produces<WorkStatisticsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
