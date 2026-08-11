using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// The order book, and how far each one has got.
/// </summary>
public static class GetOrders
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/outbound/orders", async (
                OutboundDbContext dbContext,
                OrderDescriber describe,
                CancellationToken cancellationToken,
                SalesOrderStatus? status = null,
                Guid? waveId = null,
                int page = 1,
                int pageSize = 25) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 200);
                page = Math.Max(page, 1);

                var query = dbContext.SalesOrders.AsNoTracking();

                if (status is { } wanted) query = query.Where(o => o.Status == wanted);
                if (waveId is { } wave) query = query.Where(o => o.WaveId == wave);

                var total = await query.CountAsync(cancellationToken);

                var orders = await query
                    .Include(o => o.Lines)
                    .ThenInclude(l => l.Allocations)
                    // Working order: what is open and urgent first, and a stable
                    // tiebreak so paging cannot show one order twice.
                    .OrderBy(o => o.Status)
                    .ThenBy(o => o.Priority)
                    .ThenBy(o => o.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new SalesOrderPage(
                    await describe.DescribeAsync(orders, cancellationToken), total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("GetSalesOrders")
            .WithSummary("Customer orders, open and urgent first.")
            .Produces<SalesOrderPage>();
    }
}

/// <summary>One order in full, with every commitment and the job fetching it.</summary>
public static class GetOrder
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/outbound/orders/{id:guid}", async (
                Guid id,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                CancellationToken cancellationToken) =>
            {
                var order = await dbContext.SalesOrders
                    .AsNoTracking()
                    .Include(o => o.Lines)
                    .ThenInclude(l => l.Allocations)
                    .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

                return order is null
                    ? Result.Failure<SalesOrderResponse>(OutboundErrors.OrderNotFound).ToHttpResult()
                    : Results.Ok(await describe.DescribeAsync(order, cancellationToken));
            })
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("GetSalesOrder")
            .WithSummary("One order, with its allocations and their picks.")
            .Produces<SalesOrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
