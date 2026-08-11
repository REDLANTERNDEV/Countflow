using System.Security.Claims;
using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Counting.Features.Plans;

/// <summary>
/// Raises a batch of counts by policy.
/// <para>
/// The endpoint the whole phase hangs off, and it is deliberately the only way
/// counts come into being. There is no "count this bay" command, because a count
/// somebody chose by hand is a count with no policy behind it — the trace would be
/// empty, the accuracy figures would be measuring wherever a supervisor's attention
/// happened to fall, and the coverage claim would be worthless. If the rules will
/// not raise a bay, the answer is to change the rules and see what else changes
/// with it.
/// </para>
/// </summary>
public static class CreateCountPlan
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/counting/plans", async (
                CreateCountPlanRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                CountPlanning planning,
                CountPlanDescriber describe,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        request, user, idempotencyKey, planning, describe, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Counting")
            .WithName("CreateCountPlan")
            .WithSummary("Raise a batch of cycle counts chosen by the counting policy.")
            .Produces<CountPlanResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<CountPlanResponse>> HandleAsync(
        CreateCountPlanRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        CountPlanning planning,
        CountPlanDescriber describe,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<CountPlanResponse>(actor.Error);

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<CountPlanResponse>(CountingErrors.WarehouseNotFound);

        var plan = await planning.PlanAsync(
            warehouse.Id,
            request.MaxTasks,
            request.Notes,
            idempotencyKey,
            actor.Value,
            cancellationToken);

        return plan.IsFailure
            ? Result.Failure<CountPlanResponse>(plan.Error)
            : await describe.DescribeAsync(plan.Value, cancellationToken);
    }
}

/// <summary>The plans a warehouse has run, newest first.</summary>
public static class GetCountPlans
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/counting/plans", async (
                Guid? warehouseId,
                CountPlanStatus? status,
                int? page,
                int? pageSize,
                CountingDbContext dbContext,
                CountPlanDescriber describe,
                CancellationToken cancellationToken) =>
            {
                var currentPage = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? 25, 1, 100);

                // Tracked, unusually for a listing, because describing a plan closes
                // the ones whose work has finished — see CountPlanDescriber for why
                // that happens on read rather than on a signal from the queue.
                var query = dbContext.CountPlans
                    .Include(p => p.Lines)
                    .Where(p => (warehouseId == null || p.WarehouseId == warehouseId)
                                && (status == null || p.Status == status));

                var total = await query.CountAsync(cancellationToken);

                var plans = await query
                    .OrderByDescending(p => p.CreatedAt)
                    .Skip((currentPage - 1) * size)
                    .Take(size)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new CountPlanPage(
                    await describe.SummariseAsync(plans, cancellationToken),
                    total,
                    currentPage,
                    size));
            })
            .RequireAuthorization()
            .WithTags("Counting")
            .WithName("GetCountPlans")
            .WithSummary("List count plans, newest first.")
            .Produces<CountPlanPage>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}

/// <summary>One plan, with every holding it asked about and what came back.</summary>
public static class GetCountPlan
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/counting/plans/{id:guid}", async (
                Guid id,
                CountingDbContext dbContext,
                CountPlanDescriber describe,
                CancellationToken cancellationToken) =>
            {
                var plan = await dbContext.CountPlans
                    .Include(p => p.Lines)
                    .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

                return plan is null
                    ? Result.Failure<CountPlanResponse>(CountingErrors.PlanNotFound).ToHttpResult()
                    : Results.Ok(await describe.DescribeAsync(plan, cancellationToken));
            })
            .RequireAuthorization()
            .WithTags("Counting")
            .WithName("GetCountPlan")
            .WithSummary("One count plan and its holdings.")
            .Produces<CountPlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

/// <summary>
/// Withdraws a plan's outstanding counts.
/// <para>
/// Refused when an operator is holding one, exactly as cancelling a sales order is.
/// Somebody halfway to a rack is not something a supervisor gets to withdraw out
/// from under them, and the count they are about to confirm has to have a plan to
/// belong to.
/// </para>
/// </summary>
public static class CancelCountPlan
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/counting/plans/{id:guid}/cancel", async (
                Guid id,
                CancelCountPlanRequest? request,
                CountingDbContext dbContext,
                IWorkQueue queue,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(id, request, dbContext, queue, clock, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Counting")
            .WithName("CancelCountPlan")
            .WithSummary("Withdraw a plan's outstanding counts.")
            .Produces<CancelCountPlanResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<CancelCountPlanResponse>> HandleAsync(
        Guid id,
        CancelCountPlanRequest? request,
        CountingDbContext dbContext,
        IWorkQueue queue,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var plan = await dbContext.CountPlans
            .Include(p => p.Lines)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (plan is null) return Result.Failure<CancelCountPlanResponse>(CountingErrors.PlanNotFound);

        if (plan.Status != CountPlanStatus.Open)
        {
            return Result.Failure<CancelCountPlanResponse>(CountingErrors.PlanNotOpen);
        }

        var taskIds = plan.Lines
            .Where(line => line.WorkTaskId is not null)
            .Select(line => line.WorkTaskId!.Value)
            .ToList();

        var states = await queue.FindManyAsync(taskIds, cancellationToken);

        var claimed = states.Values.Count(summary => summary.State == WorkTaskState.Assigned);
        if (claimed > 0)
        {
            return Result.Failure<CancelCountPlanResponse>(CountingErrors.CountsInProgress(claimed));
        }

        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? $"Count plan {plan.Reference} cancelled"
            : request.Reason;

        var withdrawn = await queue.CancelAsync(taskIds, reason, cancellationToken);
        if (withdrawn.IsFailure) return Result.Failure<CancelCountPlanResponse>(withdrawn.Error);

        plan.Status = CountPlanStatus.Cancelled;
        plan.CancelledReason = reason;
        plan.CompletedAt = clock.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CancelCountPlanResponse(plan.Id, plan.Reference, withdrawn.Value);
    }
}

public sealed record CancelCountPlanRequest(string? Reason = null);

public sealed record CancelCountPlanResponse(Guid Id, string Reference, int WithdrawnCount);
