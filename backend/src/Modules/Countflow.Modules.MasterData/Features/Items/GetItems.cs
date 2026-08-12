using System.Linq.Expressions;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Features.Locations;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Items;

public sealed record PackConfigurationResponse(
    string Level,
    int BaseUnitQuantity,
    string? Gtin,
    decimal? LengthMm,
    decimal? WidthMm,
    decimal? HeightMm,
    decimal? GrossWeightKg,
    decimal? VolumeM3,
    bool IsReceivingUnit,
    bool IsPickUnit);

public sealed record ItemResponse(
    Guid Id,
    string Sku,
    string Description,
    string BaseUom,
    string AbcClass,
    string TemperatureClass,
    bool IsLotTracked,
    bool IsSerialTracked,
    bool IsExpiryTracked,
    int? ShelfLifeDays,
    bool IsHazardous,
    bool IsActive,
    IReadOnlyList<PackConfigurationResponse> PackConfigurations);

public static class GetItems
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/items", async (
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken,
                string? search = null,
                AbcClass? abcClass = null,
                TemperatureClass? temperature = null,
                int page = 1,
                int pageSize = 50) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 200);
                page = Math.Max(page, 1);

                var query = dbContext.Items.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim();

                    // Searching by GTIN matters as much as by SKU: when a scan
                    // fails, the first thing anyone does is type the barcode in.
                    query = query.Where(i =>
                        EF.Functions.ILike(i.Sku, $"%{term}%")
                        || EF.Functions.ILike(i.Description, $"%{term}%")
                        || i.PackConfigurations.Any(p => p.Gtin != null && p.Gtin.Contains(term)));
                }

                if (abcClass is not null)
                {
                    query = query.Where(i => i.AbcClass == abcClass);
                }

                if (temperature is not null)
                {
                    query = query.Where(i => i.TemperatureClass == temperature);
                }

                var total = await query.CountAsync(cancellationToken);

                var items = await query
                    .OrderBy(i => i.Sku)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(Projection)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new PagedResponse<ItemResponse>(items, total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Master data")
            .WithName("GetItems")
            .WithSummary("List items, filtered and paged.")
            .Produces<PagedResponse<ItemResponse>>();

        endpoints.MapGet("/items/{sku}", async (
                string sku,
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var item = await dbContext.Items
                    .AsNoTracking()
                    .Where(i => i.Sku == sku)
                    .Select(Projection)
                    .SingleOrDefaultAsync(cancellationToken);

                return item is null
                    ? Result.Failure<ItemResponse>(
                        Error.NotFound("item.not_found", $"No item with SKU '{sku}'.")).ToHttpResult()
                    : Results.Ok(item);
            })
            .RequireAuthorization()
            .WithTags("Master data")
            .WithName("GetItemBySku")
            .WithSummary("Fetch a single item with its full packaging hierarchy.")
            .Produces<ItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Shared projection, held as an <see cref="Expression"/> rather than a
    /// method.
    /// <para>
    /// This distinction is not cosmetic. A plain method inside <c>Select</c>
    /// cannot be translated to SQL, so EF materialises the entities and runs it
    /// client-side — where <c>PackConfigurations</c> is empty unless it was
    /// explicitly included. The result is an endpoint that returns items with no
    /// packaging and no error at all. As an expression it composes into the
    /// query and the collection is fetched properly.
    /// </para>
    /// </summary>
    private static readonly Expression<Func<Item, ItemResponse>> Projection = i =>
        new ItemResponse(
            i.Id,
            i.Sku,
            i.Description,
            i.BaseUom,
            i.AbcClass.ToString(),
            i.TemperatureClass.ToString(),
            i.IsLotTracked,
            i.IsSerialTracked,
            i.IsExpiryTracked,
            i.ShelfLifeDays,
            i.IsHazardous,
            i.IsActive,
            i.PackConfigurations
                .OrderBy(p => p.Level)
                .Select(p => new PackConfigurationResponse(
                    p.Level.ToString(),
                    p.BaseUnitQuantity,
                    p.Gtin,
                    p.LengthMm,
                    p.WidthMm,
                    p.HeightMm,
                    p.GrossWeightKg,
                    p.LengthMm != null && p.WidthMm != null && p.HeightMm != null
                        ? p.LengthMm * p.WidthMm * p.HeightMm / 1_000_000_000m
                        : null,
                    p.IsReceivingUnit,
                    p.IsPickUnit))
                .ToList());
}
