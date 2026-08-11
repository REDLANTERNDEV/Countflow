using System.Security.Claims;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// Commits stock to an order and puts the picks in somebody's queue.
/// <para>
/// Releasing an already-released order again is allowed and is the sanctioned way
/// to chase a shortfall: the lines that are fully committed are left alone, and
/// the ones the rules could not fill last time are re-evaluated against a
/// warehouse that has since taken a delivery. That is why the endpoint asks for
/// an idempotency key rather than refusing a second call — the key is what tells
/// a retry of <i>this</i> release from a deliberate second attempt at the
/// shortfall.
/// </para>
/// </summary>
public static class ReleaseOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outbound/orders/{id:guid}/release", async (
                Guid id,
                ReleaseOrderRequest? request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                OutboundDbContext dbContext,
                OrderRelease release,
                OrderDescriber describe,
                ILocationDirectory locations,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, request, user, idempotencyKey, dbContext, release, describe, locations,
                        cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("ReleaseSalesOrder")
            .WithSummary("Allocate stock by rule and raise the picks.")
            .Produces<ReleaseOrderResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<ReleaseOrderResponse>> HandleAsync(
        Guid id,
        ReleaseOrderRequest? request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        OutboundDbContext dbContext,
        OrderRelease release,
        OrderDescriber describe,
        ILocationDirectory locations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReleaseOrderResponse>(OutboundErrors.IdempotencyKeyRequired);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<ReleaseOrderResponse>(actor.Error);

        var order = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null) return Result.Failure<ReleaseOrderResponse>(OutboundErrors.OrderNotFound);

        if (!string.IsNullOrWhiteSpace(request?.StagingLocationCode))
        {
            var staging = await CreateOrder.ResolveStagingAsync(
                locations, request.StagingLocationCode, cancellationToken);

            if (staging.IsFailure) return Result.Failure<ReleaseOrderResponse>(staging.Error);

            order.StagingLocationId = staging.Value;
        }

        var released = await release.ReleaseAsync(order, idempotencyKey, actor.Value, cancellationToken);
        if (released.IsFailure) return Result.Failure<ReleaseOrderResponse>(released.Error);

        return new ReleaseOrderResponse(
            await describe.DescribeAsync(order, cancellationToken),
            released.Value.Allocations,
            released.Value.Tasks,
            released.Value.Shortfall);
    }
}
