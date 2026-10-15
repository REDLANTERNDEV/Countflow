using Countflow.SharedKernel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
namespace Countflow.Modules.Inventory.Features.Accuracy;

public sealed record ZoneDensitySlice(Guid? ZoneId, string? AreaCode, string? ZoneCode, int Locations, int Occupied,
    decimal? OccupancyRate, int KnownCapacityLocations, decimal? CubeUtilization);
public sealed record ZoneDensityResponse(DateTimeOffset ObservedAt, ZoneDensitySlice Overall, IReadOnlyList<ZoneDensitySlice> Zones);
public static class GetZoneDensity
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/zone-density", async (Guid warehouseId, ILocationDirectory locations, IStockQuery stock,
            IWarehouseDirectory warehouses, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (await warehouses.FindAsync(warehouseId, cancellationToken) is not { IsActive: true }) return Results.NotFound();
            var eligible = (await locations.FindStorageCandidatesAsync(warehouseId, cancellationToken))
                .Where(location => location.IsActive && location.IsHierarchyActive && location.Role is StorageRole.Reserve or StorageRole.Forward).ToList();
            var occupancy = await stock.OccupancyAsync(eligible.Select(location => location.Id).ToList(), Guid.Empty, null, cancellationToken);
            ZoneDensitySlice Slice(IReadOnlyList<StorageLocationSnapshot> group, Guid? zoneId, string? area, string? zone)
            {
                var occupied = group.Count(location => occupancy.GetValueOrDefault(location.Id)?.TotalUnits > 0);
                var knownCapacity = group.Count(location => location.MaxVolumeM3 > 0);
                decimal? ratio = null;
                if (group.Count > 0 && knownCapacity == group.Count && group.All(location => !occupancy.TryGetValue(location.Id, out var holding) || holding.OccupiedVolumeM3 != null))
                    ratio = Math.Round(group.Sum(location => occupancy.GetValueOrDefault(location.Id)?.OccupiedVolumeM3 ?? 0) / group.Sum(location => location.MaxVolumeM3!.Value), 4);
                return new(zoneId, area, zone, group.Count, occupied, group.Count == 0 ? null : Math.Round((decimal)occupied / group.Count, 4), knownCapacity, ratio);
            }
            var zones = eligible.GroupBy(location => location.ZoneId).Select(group => Slice(group.ToList(), group.Key, group.First().AreaCode, group.First().ZoneCode))
                .OrderBy(zone => zone.AreaCode).ThenBy(zone => zone.ZoneCode).ThenBy(zone => zone.ZoneId).ToList();
            return Results.Ok(new ZoneDensityResponse(clock.GetUtcNow(), Slice(eligible, null, null, null), zones));
        }).RequireAuthorization().WithTags("Inventory").WithName("GetZoneDensity").WithSummary("Current storage-slot occupancy and fully measured cube utilization by zone.")
            .Produces<ZoneDensityResponse>().ProducesProblem(404);
    }
}
