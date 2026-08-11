using System.Security.Claims;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Work.Features;

/// <summary>
/// The work queue, for the supervisor console and for an operator checking what
/// they are holding.
/// </summary>
public static class GetWorkTasks
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/work/tasks", async (
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                ClaimsPrincipal user,
                CancellationToken cancellationToken,
                WorkTaskStatus? status = null,
                WorkTaskKind? kind = null,
                bool mine = false,
                int page = 1,
                int pageSize = 50) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 200);
                page = Math.Max(page, 1);

                var query = dbContext.WorkTasks.AsNoTracking();

                if (status is { } wanted) query = query.Where(t => t.Status == wanted);
                if (kind is { } wantedKind) query = query.Where(t => t.Kind == wantedKind);

                if (mine)
                {
                    var actor = PostingActor.From(user);
                    if (actor.IsFailure)
                    {
                        return Result.Failure<WorkTaskPage>(actor.Error).ToHttpResult();
                    }

                    query = query.Where(t => t.AssignedToUserId == actor.Value.UserId);
                }

                var total = await query.CountAsync(cancellationToken);

                var rows = await query
                    // Same order the queue is worked in, so the console shows what
                    // the handheld will hand out next rather than a different list
                    // that happens to contain the same tasks.
                    .OrderBy(t => t.Status)
                    .ThenBy(t => t.Priority)
                    .ThenBy(t => t.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new WorkTaskPage(
                    await describe.DescribeAsync(rows, cancellationToken), total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("GetWorkTasks")
            .WithSummary("Directed work, newest queue order first.")
            .Produces<WorkTaskPage>();
    }
}

/// <summary>
/// One task in full, including why the rules suggested what they did.
/// </summary>
public static class GetWorkTask
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/work/tasks/{id:guid}", async (
                Guid id,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                IDecisionTraceReader traces,
                CancellationToken cancellationToken) =>
            {
                var task = await dbContext.WorkTasks
                    .AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

                if (task is null)
                {
                    return Result.Failure<WorkTaskDetailResponse>(WorkErrors.TaskNotFound).ToHttpResult();
                }

                var decision = task.DecisionTraceId is { } traceId
                    ? await traces.FindAsync(traceId, cancellationToken)
                    : null;

                return Results.Ok(new WorkTaskDetailResponse(
                    await describe.DescribeAsync(task, cancellationToken), decision));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("GetWorkTask")
            .WithSummary("One task, with the decision trace behind its suggestion.")
            .Produces<WorkTaskDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
