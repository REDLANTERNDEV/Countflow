using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Locations;

public sealed record LocationResponse(
    Guid Id,
    string Code,
    string Type,
    string? ZoneCode,
    string? AreaCode,
    string? Aisle,
    string? Bay,
    string? Level,
    string? Position,
    int PickSequence,
    decimal? MaxVolumeM3,
    decimal? MaxWeightKg,
    bool IsPickable,
    bool IsReplenishable,
    bool IsActive);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class GetLocations
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/locations", async (
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken,
                string? search = null,
                LocationType? type = null,
                string? zone = null,
                bool? pickable = null,
                int page = 1,
                int pageSize = 50) =>
            {
                // A warehouse has thousands of bins, so this endpoint is paged
                // and filtered by default rather than ever returning the lot.
                pageSize = Math.Clamp(pageSize, 1, 500);
                page = Math.Max(page, 1);

                var query = dbContext.Locations.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    query = query.Where(l => EF.Functions.ILike(l.Code, $"%{search.Trim()}%"));
                }

                if (type is not null)
                {
                    query = query.Where(l => l.Type == type);
                }

                if (!string.IsNullOrWhiteSpace(zone))
                {
                    query = query.Where(l => l.Zone != null && l.Zone.Code == zone);
                }

                if (pickable is not null)
                {
                    query = query.Where(l => l.IsPickable == pickable);
                }

                var total = await query.CountAsync(cancellationToken);

                var locations = await query
                    .OrderBy(l => l.PickSequence)
                    .ThenBy(l => l.Code)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(l => new LocationResponse(
                        l.Id,
                        l.Code,
                        l.Type.ToString(),
                        l.Zone!.Code,
                        l.Zone!.Area!.Code,
                        l.Aisle,
                        l.Bay,
                        l.Level,
                        l.Position,
                        l.PickSequence,
                        l.MaxVolumeM3,
                        l.MaxWeightKg,
                        l.IsPickable,
                        l.IsReplenishable,
                        l.IsActive))
                    .ToListAsync(cancellationToken);

                return Results.Ok(new PagedResponse<LocationResponse>(locations, total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Master data")
            .WithName("GetLocations")
            .WithSummary("List locations, filtered and paged.")
            .Produces<PagedResponse<LocationResponse>>();
    }
}
