using System.Security.Claims;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Work.Features;

/// <param name="WarehouseId">
/// Optional. A single-site deployment — which is what Countflow is built for —
/// leaves it out and takes whatever is next.
/// </param>
public sealed record ClaimNextTaskRequest(Guid? WarehouseId = null, WorkTaskKind? Kind = null);

/// <summary>
/// Hands an operator their next job and marks it theirs, in one statement.
/// <para>
/// <c>FOR UPDATE SKIP LOCKED</c> rather than select-then-update, and this is the
/// one place in the module where raw SQL earns its keep. Thirty operators press
/// "next job" within the same few seconds at the start of a shift; a
/// read-then-write pair hands the same task to several of them and the losers
/// walk to a bin somebody has already emptied. <c>SKIP LOCKED</c> lets each
/// claimant step over the rows its rivals are taking and get the next one down,
/// with no retry loop and no contention.
/// </para>
/// </summary>
public static class ClaimNextTask
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tasks/claim-next", async (
                ClaimNextTaskRequest? request,
                ClaimsPrincipal user,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                IDecisionTraceReader traces,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var actor = PostingActor.From(user);
                if (actor.IsFailure)
                {
                    return Result.Failure<WorkTaskDetailResponse>(actor.Error).ToHttpResult();
                }

                var claimed = await ClaimAsync(
                    dbContext, actor.Value, request, clock.GetUtcNow(), cancellationToken);

                if (claimed is null)
                {
                    return Result.Failure<WorkTaskDetailResponse>(WorkErrors.NoWorkAvailable).ToHttpResult();
                }

                var task = await dbContext.WorkTasks
                    .AsNoTracking()
                    .SingleAsync(t => t.Id == claimed.Value, cancellationToken);

                var decision = task.DecisionTraceId is { } traceId
                    ? await traces.FindAsync(traceId, cancellationToken)
                    : null;

                return Results.Ok(new WorkTaskDetailResponse(
                    await describe.DescribeAsync(task, cancellationToken), decision));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("ClaimNextTask")
            .WithSummary("Take the next job in the queue.")
            .Produces<WorkTaskDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Guid?> ClaimAsync(
        WorkDbContext dbContext,
        PostingActor actor,
        ClaimNextTaskRequest? request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {WorkDbContext.SchemaName}."WorkTasks"
            SET "Status" = @assigned,
                "AssignedToUserId" = @userId,
                "AssignedToUserName" = @userName,
                "AssignedAt" = @now
            WHERE "Id" = (
                SELECT "Id"
                FROM {WorkDbContext.SchemaName}."WorkTasks"
                WHERE "Status" = @ready
                  AND (@warehouseId IS NULL OR "WarehouseId" = @warehouseId)
                  AND (@kind IS NULL OR "Kind" = @kind)
                ORDER BY "Priority", "CreatedAt"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING "Id" AS "Value"
            """;

        var claimed = await dbContext.Database
            .SqlQueryRaw<Guid>(
                sql,
                new NpgsqlParameter("assigned", NpgsqlDbType.Integer) { Value = (int)WorkTaskStatus.Assigned },
                new NpgsqlParameter("ready", NpgsqlDbType.Integer) { Value = (int)WorkTaskStatus.Ready },
                new NpgsqlParameter("userId", NpgsqlDbType.Uuid) { Value = actor.UserId },
                new NpgsqlParameter("userName", NpgsqlDbType.Text) { Value = actor.UserName },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("warehouseId", NpgsqlDbType.Uuid)
                {
                    Value = request?.WarehouseId is { } warehouse ? warehouse : DBNull.Value
                },
                new NpgsqlParameter("kind", NpgsqlDbType.Integer)
                {
                    Value = request?.Kind is { } kind ? (int)kind : DBNull.Value
                })
            .ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }
}

/// <summary>
/// Takes one named task.
/// <para>
/// The queue hands out the next job, but an operator standing in front of a
/// pallet wants <i>that</i> pallet, and a supervisor clearing a blocked dock
/// wants to pick. Claiming by id is the same state change as claim-next with the
/// selection made by a person instead of by priority.
/// </para>
/// </summary>
public static class ClaimTask
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tasks/{id:guid}/claim", async (
                Guid id,
                ClaimsPrincipal user,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                IDecisionTraceReader traces,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var actor = PostingActor.From(user);
                if (actor.IsFailure)
                {
                    return Result.Failure<WorkTaskDetailResponse>(actor.Error).ToHttpResult();
                }

                var task = await dbContext.WorkTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (task is null)
                {
                    return Result.Failure<WorkTaskDetailResponse>(WorkErrors.TaskNotFound).ToHttpResult();
                }

                if (task.Status == WorkTaskStatus.Assigned && task.AssignedToUserId != actor.Value.UserId)
                {
                    return Result.Failure<WorkTaskDetailResponse>(WorkErrors.TaskAlreadyClaimed).ToHttpResult();
                }

                if (task.Status is not (WorkTaskStatus.Ready or WorkTaskStatus.Assigned))
                {
                    return Result.Failure<WorkTaskDetailResponse>(WorkErrors.TaskNotOpen).ToHttpResult();
                }

                task.Status = WorkTaskStatus.Assigned;
                task.AssignedToUserId = actor.Value.UserId;
                task.AssignedToUserName = actor.Value.UserName;
                task.AssignedAt ??= clock.GetUtcNow();

                await dbContext.SaveChangesAsync(cancellationToken);

                var decision = task.DecisionTraceId is { } traceId
                    ? await traces.FindAsync(traceId, cancellationToken)
                    : null;

                return Results.Ok(new WorkTaskDetailResponse(
                    await describe.DescribeAsync(task, cancellationToken), decision));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("ClaimTask")
            .WithSummary("Take one named task.")
            .Produces<WorkTaskDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>
/// Puts a claimed task back. An operator who cannot hand work back is an operator
/// who abandons it, and an abandoned task is invisible until somebody notices the
/// pallet is still on the dock.
/// </summary>
public static class ReleaseTask
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tasks/{id:guid}/release", async (
                Guid id,
                ClaimsPrincipal user,
                WorkDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var actor = PostingActor.From(user);
                if (actor.IsFailure) return actor.ToHttpResult();

                var task = await dbContext.WorkTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (task is null) return Result.Failure(WorkErrors.TaskNotFound).ToHttpResult();

                if (task.Status != WorkTaskStatus.Assigned)
                {
                    return Result.Failure(WorkErrors.TaskNotOpen).ToHttpResult();
                }

                if (task.AssignedToUserId != actor.Value.UserId)
                {
                    return Result.Failure(WorkErrors.TaskNotYours).ToHttpResult();
                }

                task.Status = WorkTaskStatus.Ready;
                task.AssignedToUserId = null;
                task.AssignedToUserName = null;
                task.AssignedAt = null;

                await dbContext.SaveChangesAsync(cancellationToken);

                return Result.Success().ToHttpResult();
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("ReleaseTask")
            .WithSummary("Hand a claimed task back to the queue.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
