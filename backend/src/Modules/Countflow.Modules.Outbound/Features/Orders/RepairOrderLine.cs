using System.Security.Claims;
using System.Text.Json;
using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Countflow.Modules.Outbound.Features.Orders;

public static class RepairOrderLine
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/outbound/orders/{id:guid}/lines/{lineId:guid}/repair", async (
                Guid id, Guid lineId, RepairOrderLineRequest request, ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? key, OrderRepair repair,
                CancellationToken cancellationToken) =>
                (await repair.RepairAsync(id, lineId, request, user, key, cancellationToken)).ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .AddEndpointFilter<OrderMutationFilter>()
            .WithTags("Outbound")
            .WithName("RepairSalesOrderLine")
            .WithSummary("Withdraw an unclaimed stranded commitment and reallocate that order line by rule.")
            .Produces<RepairOrderLineResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

public sealed class OrderRepair(
    OutboundDbContext db, IStockReservations reservations, IWorkQueue work,
    IAllocationAdvisor advisor, OrderRelease release, OrderDescriber describe, TimeProvider clock)
{
    public static Task<bool> HasPendingAsync(OutboundDbContext db, Guid orderId, CancellationToken ct) =>
        (from repair in db.OrderLineRepairs
         join line in db.SalesOrderLines on repair.SalesOrderLineId equals line.Id
         where line.SalesOrderId == orderId && repair.CompletedAt == null
         select repair.Id).AnyAsync(ct);

    public async Task<Result<RepairOrderLineResponse>> RepairAsync(
        Guid orderId, Guid lineId, RepairOrderLineRequest request, ClaimsPrincipal user,
        string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.IdempotencyKeyRequired);
        if (key.Length > 128)
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.RepairKeyInvalid);
        var reason = request.Reason.Trim();
        if (reason.Length is 0 or > 500)
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.RepairReasonRequired);
        var currentActor = PostingActor.From(user);
        if (currentActor.IsFailure) return Result.Failure<RepairOrderLineResponse>(currentActor.Error);

        var order = await db.SalesOrders.Include(row => row.Lines).ThenInclude(line => line.Allocations)
            .SingleOrDefaultAsync(row => row.Id == orderId, ct);
        if (order is null) return Result.Failure<RepairOrderLineResponse>(OutboundErrors.OrderNotFound);
        var line = order.Lines.SingleOrDefault(row => row.Id == lineId);
        if (line is null) return Result.Failure<RepairOrderLineResponse>(OutboundErrors.OrderLineNotFound);
        var repair = await db.OrderLineRepairs.SingleOrDefaultAsync(row => row.IdempotencyKey == key, ct);
        if (repair is not null && (repair.SalesOrderLineId != lineId || repair.Reason != reason))
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.IdempotencyKeyReused);
        if (repair?.CompletedAt is not null)
            return await ResponseAsync(order, repair, wasAlready: true, ct);
        if (order.Status != SalesOrderStatus.Released)
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.OrderNotReleasable(order.Status.ToString()));
        if (order.StagingLocationId is null)
            return Result.Failure<RepairOrderLineResponse>(OutboundErrors.StagingLocationRequired);

        if (repair is null)
        {
            if (await HasPendingAsync(db, orderId, ct))
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.RepairInProgress);
            var stranded = (await reservations.FindStrandedByOwnerAsync(lineId, ct))
                .Select(row => row.Reservation.Id).ToHashSet();
            var candidates = line.Allocations.Where(row => row.SupersededByRepairId == null
                && stranded.Contains(row.StockReservationId)).ToList();
            if (candidates.Count == 0)
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.NoStrandedCommitment);
            var ensured = await release.EnsurePicksAsync(order, line, ct, candidates.Select(row => row.Id).ToArray());
            if (ensured.IsFailure) return Result.Failure<RepairOrderLineResponse>(ensured.Error);
            await db.SaveChangesAsync(ct);
            var tasks = await work.FindManyAsync(candidates.Where(row => row.WorkTaskId != null)
                .Select(row => row.WorkTaskId!.Value).ToArray(), ct);
            if (candidates.Any(row => row.WorkTaskId is { } id && !tasks.ContainsKey(id)))
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.RepairTaskMissing);
            var claimed = tasks.Values.Count(task => task.State == WorkTaskState.Assigned);
            if (claimed > 0) return Result.Failure<RepairOrderLineResponse>(OutboundErrors.PicksInProgress(claimed));
            candidates = candidates.Where(row => row.WorkTaskId is not { } id || tasks[id].State != WorkTaskState.Completed).ToList();
            if (candidates.Count == 0)
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.NoStrandedCommitment);
            repair = new OrderLineRepair
            {
                SalesOrderLineId = lineId, IdempotencyKey = key, Reason = reason,
                AllocationIds = candidates.Select(row => row.Id).ToArray(), CreatedAt = clock.GetUtcNow(),
                RequestedByUserId = currentActor.Value.UserId, RequestedByUserName = currentActor.Value.UserName
            };
            db.OrderLineRepairs.Add(repair);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.IdempotencyKeyReused);
            }
        }

        if (!repair.WithdrawalComplete)
        {
            var targets = line.Allocations.Where(row => repair.AllocationIds.Contains(row.Id)).ToList();
            var ensured = await release.EnsurePicksAsync(order, line, ct, repair.AllocationIds);
            if (ensured.IsFailure) return Result.Failure<RepairOrderLineResponse>(ensured.Error);
            await db.SaveChangesAsync(ct);
            var taskIds = targets.Where(row => row.WorkTaskId != null).Select(row => row.WorkTaskId!.Value).ToArray();
            var cancelled = await work.CancelAsync(taskIds, $"outbound.commitment_repair:{repair.Id:N}", ct);
            if (cancelled.IsFailure) return Result.Failure<RepairOrderLineResponse>(cancelled.Error);
            var tasks = await work.FindManyAsync(taskIds, ct);
            if (targets.Any(row => row.WorkTaskId is { } id && !tasks.ContainsKey(id)))
                return Result.Failure<RepairOrderLineResponse>(OutboundErrors.RepairTaskMissing);
            // A pick may have completed before CancelAsync acquired its locks.
            // Its goods and original commitment history must remain on this order.
            targets = targets.Where(row => row.WorkTaskId is not { } id || tasks[id].State == WorkTaskState.Cancelled).ToList();
            var released = await reservations.ReleaseAsync(targets.Select(row => row.StockReservationId).ToArray(),
                $"outbound.commitment_repair:{repair.Id:N}", ct);
            if (released.IsFailure) return Result.Failure<RepairOrderLineResponse>(released.Error);
            foreach (var target in targets) target.SupersededByRepairId = repair.Id;
            repair.ReservationsReleased = targets.Count;
            repair.TasksCancelled = targets.Count(row => row.WorkTaskId != null);
            line.AllocatedQuantity = line.Allocations.Where(row => row.SupersededByRepairId == null).Sum(row => row.Quantity);
            repair.WithdrawalComplete = true;
            await db.SaveChangesAsync(ct);
        }

        var actor = new PostingActor(repair.RequestedByUserId, repair.RequestedByUserName);
        if (repair.AllocationPlanJson is null && line.OutstandingQuantity > 0)
        {
            var advised = await advisor.AllocateAsync(new AllocationQuery
            {
                WarehouseId = order.WarehouseId, ItemId = line.ItemId, RequiredLotId = line.RequiredLotId,
                Quantity = line.OutstandingQuantity, ShipBy = order.ShipBy, Actor = actor
            }, ct);
            if (advised.IsFailure) return Result.Failure<RepairOrderLineResponse>(advised.Error);
            repair.AllocationPlanJson = JsonSerializer.Serialize(advised.Value);
            await db.SaveChangesAsync(ct);
        }
        var releaseKey = $"repair:{repair.Id:N}";
        if (repair.AllocationPlanJson is { } json)
        {
            var plan = JsonSerializer.Deserialize<AllocationPlan>(json)!;
            var applied = await release.ApplyRepairPlanAsync(order, line, releaseKey, actor, plan, ct);
            if (applied.IsFailure) return Result.Failure<RepairOrderLineResponse>(applied.Error);
        }
        var replacements = line.Allocations.Where(row => row.ReleaseIdempotencyKey == releaseKey).ToList();
        repair.AllocationsRaised = replacements.Count;
        repair.TasksRaised = replacements.Count(row => row.WorkTaskId != null);
        repair.ShortfallQuantity = line.OutstandingQuantity;
        repair.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await ResponseAsync(order, repair, wasAlready: false, ct);
    }

    private async Task<RepairOrderLineResponse> ResponseAsync(SalesOrder order, OrderLineRepair repair, bool wasAlready, CancellationToken ct) =>
        new(await describe.DescribeAsync(order, ct), repair.ReservationsReleased, repair.TasksCancelled,
            repair.AllocationsRaised, repair.TasksRaised, repair.ShortfallQuantity, wasAlready);
}
