using System.Security.Claims;
using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// Cancels an order and gives its stock back.
/// <para>
/// The step that closes the reservation lifecycle, and the reason
/// available-to-promise stays honest over a week rather than only over an hour: an
/// order cancelled without releasing its holds leaves stock committed to nothing,
/// invisible to every future allocation, and nobody notices until the warehouse
/// is mysteriously unable to promise goods it can see on the shelf.
/// </para>
/// <para>
/// <b>Withdraw the work first, then release the stock.</b> A claimed pick means an
/// operator is walking towards a rack, and pulling the claim out from under them
/// would send them to fetch stock against a reservation that no longer exists —
/// so the cancellation refuses while any job is held rather than racing the
/// person holding it. Jobs nobody has started are withdrawn, and only then does
/// the stock go back.
/// </para>
/// <para>
/// A despatched order cannot be cancelled. The trailer has gone; the correction
/// for that is a customer return, which is a receipt.
/// </para>
/// </summary>
public static class CancelOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outbound/orders/{id:guid}/cancel", async (
                Guid id,
                CancelOrderRequest request,
                ClaimsPrincipal user,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                IStockReservations reservations,
                IWorkQueue work,
                ILogger<SalesOrder> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, request, user, dbContext, describe, reservations, work, logger, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("CancelSalesOrder")
            .WithSummary("Cancel an order, withdraw its picks and release its stock.")
            .Produces<CancelOrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<CancelOrderResponse>> HandleAsync(
        Guid id,
        CancelOrderRequest request,
        ClaimsPrincipal user,
        OutboundDbContext dbContext,
        OrderDescriber describe,
        IStockReservations reservations,
        IWorkQueue work,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<CancelOrderResponse>(actor.Error);

        var order = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null) return Result.Failure<CancelOrderResponse>(OutboundErrors.OrderNotFound);

        if (order.Status == SalesOrderStatus.Cancelled)
        {
            return new CancelOrderResponse(
                await describe.DescribeAsync(order, cancellationToken), 0, 0);
        }

        if (order.Status is SalesOrderStatus.Despatched or SalesOrderStatus.Packed)
        {
            return Result.Failure<CancelOrderResponse>(
                OutboundErrors.OrderNotCancellable(order.Status.ToString()));
        }

        var allocations = order.Lines.SelectMany(l => l.Allocations).ToList();

        var taskIds = allocations
            .Where(a => a.WorkTaskId is not null)
            .Select(a => a.WorkTaskId!.Value)
            .ToList();

        var tasks = await work.FindManyAsync(taskIds, cancellationToken);

        var claimed = tasks.Values.Count(t => t.State == WorkTaskState.Assigned);
        if (claimed > 0)
        {
            return Result.Failure<CancelOrderResponse>(OutboundErrors.PicksInProgress(claimed));
        }

        var cancelled = await work.CancelAsync(taskIds, $"outbound.order_cancelled: {request.Reason}", cancellationToken);
        if (cancelled.IsFailure) return Result.Failure<CancelOrderResponse>(cancelled.Error);

        // Only the claims still standing. A pick that was confirmed already
        // discharged its reservation, and the stock it moved is at the bench — a
        // cancellation does not walk it back to the rack, and pretending to would
        // put the ledger at odds with the building.
        var released = await reservations.ReleaseAsync(
            [.. allocations.Select(a => a.StockReservationId)],
            $"outbound.order_cancelled: {request.Reason}",
            cancellationToken);

        if (released.IsFailure) return Result.Failure<CancelOrderResponse>(released.Error);

        order.Status = SalesOrderStatus.Cancelled;
        order.CancelledReason = request.Reason;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Cancelled {Reference}: {Tasks} picks withdrawn, {Reservations} holds released ({Reason})",
            order.Reference,
            cancelled.Value,
            released.Value,
            request.Reason);

        return new CancelOrderResponse(
            await describe.DescribeAsync(order, cancellationToken),
            released.Value,
            cancelled.Value);
    }
}
