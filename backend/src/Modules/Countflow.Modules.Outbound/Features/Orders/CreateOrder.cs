using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// Takes an order.
/// <para>
/// Supervisor-and-above, for the mirror of the reason an advice note is: an order
/// is what a despatch is checked against, so anyone who can invent one can make
/// stock leave the building and reconcile perfectly. In a live installation most
/// orders arrive over EDI or from an ERP; this endpoint covers the cases those
/// never do — a customer who phones, and a demo.
/// </para>
/// </summary>
public static class CreateOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outbound/orders", async (
                CreateOrderRequest request,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                IItemDirectory items,
                ILocationDirectory locations,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        request, dbContext, describe, items, locations, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("CreateSalesOrder")
            .WithSummary("Take a customer order.")
            .Produces<SalesOrderResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<SalesOrderResponse>> HandleAsync(
        CreateOrderRequest request,
        OutboundDbContext dbContext,
        OrderDescriber describe,
        IItemDirectory items,
        ILocationDirectory locations,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        if (request.Lines.Count == 0)
        {
            return Result.Failure<SalesOrderResponse>(OutboundErrors.NoLines);
        }

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<SalesOrderResponse>(OutboundErrors.WarehouseNotFound);

        var reference = request.Reference.Trim();

        if (await dbContext.SalesOrders.AnyAsync(
                o => o.WarehouseId == warehouse.Id && o.Reference == reference, cancellationToken))
        {
            return Result.Failure<SalesOrderResponse>(OutboundErrors.OrderReferenceTaken);
        }

        var staging = await ResolveStagingAsync(locations, request.StagingLocationCode, cancellationToken);
        if (staging.IsFailure) return Result.Failure<SalesOrderResponse>(staging.Error);

        var order = new SalesOrder
        {
            WarehouseId = warehouse.Id,
            Reference = reference,
            CustomerName = request.CustomerName,
            StagingLocationId = staging.Value,
            ShipBy = request.ShipBy,
            Priority = request.Priority ?? 50,
            Notes = request.Notes
        };

        var lineNumber = 1;

        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0m)
            {
                return Result.Failure<SalesOrderResponse>(OutboundErrors.QuantityNotPositive);
            }

            var item = await items.FindBySkuAsync(line.Sku.Trim(), cancellationToken);
            if (item is null) return Result.Failure<SalesOrderResponse>(OutboundErrors.ItemNotFound);

            order.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = order.Id,
                LineNumber = lineNumber++,
                ItemId = item.Id,
                OrderedQuantity = line.Quantity

                // A named batch is deliberately not resolved here. Lots live in
                // Inventory and are created when goods arrive, so an order taken
                // before the batch exists could not name it — the constraint reads
                // a lot id, and wiring the lookup is Phase 5's problem alongside
                // customer-specific batch rules.
            });
        }

        dbContext.SalesOrders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await describe.DescribeAsync(order, cancellationToken);
    }

    /// <summary>
    /// A bench, a staging lane or a shipping door — somewhere picked goods can
    /// stand while an order is made up. Racking is refused: dropping a pick back
    /// into a pick face would put the stock straight back where the next
    /// allocation would find it.
    /// </summary>
    internal static async Task<Result<Guid?>> ResolveStagingAsync(
        ILocationDirectory locations,
        string? code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Success<Guid?>(null);
        }

        var location = await locations.FindStorageLocationByCodeAsync(code.Trim(), cancellationToken);
        if (location is null) return Result.Failure<Guid?>(OutboundErrors.LocationNotFound);

        return location.Role is StorageRole.Bench or StorageRole.Staging or StorageRole.Dock
            ? Result.Success<Guid?>(location.Id)
            : Result.Failure<Guid?>(OutboundErrors.NotAStagingLocation(location.Code));
    }
}
