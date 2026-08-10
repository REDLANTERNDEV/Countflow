using Countflow.Modules.MasterData.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Warehouses;

public sealed record WarehouseResponse(
    Guid Id,
    string Code,
    string Name,
    string? City,
    string? CountryCode,
    bool IsActive);

public static class GetWarehouses
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/warehouses", async (
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var warehouses = await dbContext.Warehouses
                    .AsNoTracking()
                    .OrderBy(w => w.Code)
                    .Select(w => new WarehouseResponse(w.Id, w.Code, w.Name, w.City, w.CountryCode, w.IsActive))
                    .ToListAsync(cancellationToken);

                return Results.Ok(warehouses);
            })
            .RequireAuthorization()
            .WithTags("Master data")
            .WithName("GetWarehouses")
            .WithSummary("List all warehouses.")
            .Produces<List<WarehouseResponse>>();
    }
}
