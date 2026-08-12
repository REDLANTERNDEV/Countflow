using System.Security.Claims;
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
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// Marks an order made up at the bench.
/// <para>
/// No ledger entry, and that is the point of the step rather than an omission.
/// The goods are already standing where the pickers dropped them; packing them
/// into cartons moves nothing and changes no quantity, so a movement recording it
/// would be an entry in a ledger of physical facts that asserts nothing physical
/// (ADR 0001). What it does record is that a person looked at the order and said
/// it was complete — which is exactly the claim the despatch then relies on.
/// </para>
/// <para>
/// Refused while any pick is still open, because that claim cannot be true while
/// somebody is still walking towards a rack for it.
/// </para>
/// </summary>
public static class PackOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/outbound/orders/{id:guid}/pack", async (
                Guid id,
                PackOrderRequest? request,
                ClaimsPrincipal user,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                IWorkQueue work,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(id, request, user, dbContext, describe, work, clock, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("PackSalesOrder")
            .WithSummary("Confirm the order is picked and made up at the bench.")
            .Produces<SalesOrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<SalesOrderResponse>> HandleAsync(
        Guid id,
        PackOrderRequest? request,
        ClaimsPrincipal user,
        OutboundDbContext dbContext,
        OrderDescriber describe,
        IWorkQueue work,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<SalesOrderResponse>(actor.Error);

        var order = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null) return Result.Failure<SalesOrderResponse>(OutboundErrors.OrderNotFound);

        // Packing twice is a person pressing a button twice, not a duplicated
        // shipment, so it is idempotent rather than refused.
        if (order.Status == SalesOrderStatus.Packed)
        {
            return await describe.DescribeAsync(order, cancellationToken);
        }

        if (order.Status != SalesOrderStatus.Released)
        {
            return Result.Failure<SalesOrderResponse>(
                OutboundErrors.OrderNotPackable(order.Status.ToString()));
        }

        var allocations = order.Lines.SelectMany(l => l.Allocations).ToList();

        var tasks = await work.FindManyAsync(
            [.. allocations.Where(a => a.WorkTaskId is not null).Select(a => a.WorkTaskId!.Value)],
            cancellationToken);

        var outstanding = tasks.Values.Count(t => t.State is WorkTaskState.Ready or WorkTaskState.Assigned);

        if (outstanding > 0)
        {
            return Result.Failure<SalesOrderResponse>(OutboundErrors.PicksOutstanding(outstanding));
        }

        if (!tasks.Values.Any(t => t.State == WorkTaskState.Completed))
        {
            return Result.Failure<SalesOrderResponse>(OutboundErrors.NothingPicked);
        }

        order.Status = SalesOrderStatus.Packed;
        order.PackedAt = clock.GetUtcNow();
        order.PackedByUserName = actor.Value.UserName;

        if (!string.IsNullOrWhiteSpace(request?.Notes))
        {
            order.Notes = request.Notes;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await describe.DescribeAsync(order, cancellationToken);
    }
}

/// <summary>
/// Sends the order out of the building.
/// <para>
/// The last movement of the round trip, and the one that makes the ledger sum to
/// zero: stock that entered from the Supplier counterparty leaves to the Customer
/// counterparty, and every entry in between moved it between real places. Nothing
/// was created and nothing destroyed, which is the whole claim ADR 0001 makes and
/// the thing a quantity column can never demonstrate.
/// </para>
/// <para>
/// The lines come from what was <b>picked</b>, not from what was ordered or even
/// from what was allocated. A line the rules could not fill and a pick nobody
/// confirmed are both absent from the trailer, and a despatch built from
/// intentions rather than from confirmations is how a customer is invoiced for
/// stock still sitting in the racking.
/// </para>
/// </summary>
public static class DespatchOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/outbound/orders/{id:guid}/despatch", async (
                Guid id,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                IStockPosting posting,
                IWorkQueue work,
                TimeProvider clock,
                ILogger<SalesOrder> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, user, idempotencyKey, dbContext, describe, posting, work, clock, logger,
                        cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("DespatchSalesOrder")
            .WithSummary("Post the picked stock out to the Customer counterparty.")
            .Produces<DespatchOrderResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<DespatchOrderResponse>> HandleAsync(
        Guid id,
        ClaimsPrincipal user,
        string? idempotencyKey,
        OutboundDbContext dbContext,
        OrderDescriber describe,
        IStockPosting posting,
        IWorkQueue work,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<DespatchOrderResponse>(OutboundErrors.IdempotencyKeyRequired);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<DespatchOrderResponse>(actor.Error);

        var order = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null) return Result.Failure<DespatchOrderResponse>(OutboundErrors.OrderNotFound);

        if (order.Status == SalesOrderStatus.Despatched)
        {
            // The same key is the handheld's or the console's retry of a despatch
            // that landed; a different key is a second trailer's worth of stock
            // leaving against one order, which the ledger would happily post.
            return string.Equals(order.DespatchIdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                ? new DespatchOrderResponse(await describe.DescribeAsync(order, cancellationToken), [])
                : Result.Failure<DespatchOrderResponse>(OutboundErrors.AlreadyDespatched);
        }

        if (order.Status != SalesOrderStatus.Packed)
        {
            return Result.Failure<DespatchOrderResponse>(
                OutboundErrors.OrderNotDespatchable(order.Status.ToString()));
        }

        var allocations = order.Lines.SelectMany(l => l.Allocations).ToList();

        var tasks = await work.FindManyAsync(
            [.. allocations.Where(a => a.WorkTaskId is not null).Select(a => a.WorkTaskId!.Value)],
            cancellationToken);

        // Grouped by where the goods actually are. Normally one bench and one
        // group; two only when pickers dropped an order in two places, and then
        // two despatches is the honest posting — the stock genuinely left from two
        // locations, and pretending otherwise would put the ledger at odds with
        // the building.
        var groups = allocations
            .Select(allocation => new
            {
                Allocation = allocation,
                Task = allocation.WorkTaskId is { } taskId ? tasks.GetValueOrDefault(taskId) : null
            })
            .Where(row => row.Task is { State: WorkTaskState.Completed, CompletedAtLocationId: not null })
            .GroupBy(row => row.Task!.CompletedAtLocationId!.Value)
            .OrderBy(group => group.Key)
            .ToList();

        if (groups.Count == 0)
        {
            return Result.Failure<DespatchOrderResponse>(OutboundErrors.NothingPicked);
        }

        var movements = new List<PostedMovement>();

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];

            var lines = group
                // Netted by item and batch: two picks of one batch onto one bench
                // are one thing leaving on one trailer, and posting them
                // separately would put two entries on the customer's account for a
                // single physical event.
                .GroupBy(row => (row.Allocation.LotId, ItemId: ItemOf(order, row.Allocation)))
                .Select(line => new DespatchLine(
                    line.Key.ItemId,
                    line.Key.LotId,
                    line.Sum(row => row.Allocation.Quantity)))
                .ToList();

            var posted = await posting.DespatchAsync(
                new DespatchInstruction
                {
                    FromLocationId = group.Key,
                    Lines = lines,
                    IdempotencyKey = $"{idempotencyKey}:despatch:{index:D2}",
                    Actor = actor.Value,
                    Reference = order.Reference
                },
                cancellationToken);

            if (posted.IsFailure)
            {
                return Result.Failure<DespatchOrderResponse>(posted.Error);
            }

            movements.AddRange(posted.Value);
        }

        order.Status = SalesOrderStatus.Despatched;
        order.DespatchedAt = clock.GetUtcNow();
        order.DespatchedByUserName = actor.Value.UserName;
        order.DespatchIdempotencyKey = idempotencyKey;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Despatched {Reference}: {Movements} ledger entries from {Origins} location(s) by {User}",
            order.Reference,
            movements.Count,
            groups.Count,
            actor.Value.UserName);

        return new DespatchOrderResponse(
            await describe.DescribeAsync(order, cancellationToken), movements);
    }

    private static Guid ItemOf(SalesOrder order, OrderLineAllocation allocation) =>
        order.Lines.Single(line => line.Id == allocation.SalesOrderLineId).ItemId;
}
