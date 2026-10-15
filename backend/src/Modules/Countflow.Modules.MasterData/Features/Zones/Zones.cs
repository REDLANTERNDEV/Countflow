using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Routing;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Zones;

/// <summary>
/// One way this zone could be walked, and whether it actually could.
/// </summary>
/// <param name="UnsupportedBecause">
/// Null when the zone has the geometry this strategy needs. A sentence when it does
/// not — and publishing the sentence beside the option is the point: a console that
/// merely greys out three of four choices teaches a supervisor that the feature is
/// broken, where "no location in this zone records an aisle" tells them what to go
/// and fix.
/// </param>
public sealed record PickRouteOptionResponse(
    string Code,
    bool IsSupported,
    string? UnsupportedBecause,
    bool RequiresRearCrossAisle,
    bool HasRequiredGeometry);

/// <param name="Aisles">Distinct aisles the bins in this zone record. Zero is what makes most strategies unavailable.</param>
/// <param name="Bays">Distinct bay codes, which is the depth every geometric strategy routes on.</param>
public sealed record ZoneResponse(
    Guid Id,
    string Code,
    string Name,
    string AreaCode,
    string TemperatureClass,
    int PickSequence,
    string PickRouteStrategy,
    bool HasRearCrossAisle,
    bool IsActive,
    int Aisles,
    int Bays,
    IReadOnlyList<PickRouteOptionResponse> RouteOptions);

/// <summary>
/// The zones of a warehouse, with how each one is walked and what else it could be.
/// </summary>
public static class GetZones
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/zones", async (
                MasterDataDbContext dbContext,
                ILocationDirectory locations,
                CancellationToken cancellationToken,
                string? warehouse = null) =>
            {
                var zones = await dbContext.Zones
                    .AsNoTracking()
                    .Where(z => warehouse == null || z.Area!.Warehouse!.Code == warehouse)
                    .OrderBy(z => z.PickSequence)
                    .ThenBy(z => z.Code)
                    .Select(z => new
                    {
                        z.Id,
                        z.Code,
                        z.Name,
                        AreaCode = z.Area!.Code,
                        z.TemperatureClass,
                        z.PickSequence,
                        z.PickRouteStrategy,
                        z.HasRearCrossAisle,
                        z.IsActive
                    })
                    .ToListAsync(cancellationToken);

                // One read for every zone on the page rather than one per zone. The
                // grid is what says which options are open, and it is the same query
                // whether it is asked about one zone or twenty.
                var map = await locations.FindZonePickRouteMapAsync(
                    [.. zones.Select(z => z.Id)], cancellationToken);

                return Results.Ok(zones
                    .Select(z => new ZoneResponse(
                        z.Id,
                        z.Code,
                        z.Name,
                        z.AreaCode,
                        z.TemperatureClass.ToString(),
                        z.PickSequence,
                        z.PickRouteStrategy,
                        z.HasRearCrossAisle,
                        z.IsActive,
                        map.Geometry.AisleCount(z.Id),
                        map.Geometry.BayCount(z.Id),
                        Options(map.Geometry, z.Id, z.HasRearCrossAisle)))
                    .ToList());
            })
            .RequireAuthorization()
            .WithTags("Master data")
            .WithName("GetZones")
            .WithSummary("Zones, how each is walked, and which other routes it could support.")
            .Produces<List<ZoneResponse>>();
    }

    internal static IReadOnlyList<PickRouteOptionResponse> Options(
        RouteGeometry geometry,
        Guid zoneId,
        bool hasRearCrossAisle) =>
    [
        .. PickRouteCatalogue.All.Select(strategy =>
        {
            var because = geometry.Explain(zoneId, strategy.Requires, hasRearCrossAisle);
            return new PickRouteOptionResponse(strategy.Code, because is null, because,
                strategy.Requires.RearCrossAisle,
                geometry.Explain(zoneId, strategy.Requires, rearCrossAisle: true) is null);
        })
    ];
}

/// <param name="HasRearCrossAisle">
/// Set together with the strategy rather than on its own endpoint. They are one
/// decision — "this hall is a through-aisle hall and should be traversed" — and
/// splitting them makes the refusal depend on which half was saved first.
/// </param>
public sealed record SetZonePickRouteRequest(string Strategy, bool HasRearCrossAisle);

/// <summary>
/// Chooses how a zone is walked, and refuses a choice the zone cannot support.
/// <para>
/// <b>Validated against the real catalogue and the real grid, here, at the moment
/// somebody presses save.</b> That is the third time this codebase has made the same
/// move: a rule set is validated by building it through the catalogue that will run
/// it, a label template by rendering it against a specimen, and now a routing
/// strategy by asking the zone's own bins whether they record what it needs. The
/// failure mode being avoided is identical in all three — an unvalidated setting
/// fails at a rack, in front of an operator, with a shift half started.
/// </para>
/// </summary>
public static class SetZonePickRoute
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/zones/{id:guid}/pick-route", async (
                Guid id,
                SetZonePickRouteRequest request,
                MasterDataDbContext dbContext,
                ILocationDirectory locations,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(id, request, dbContext, locations, cancellationToken)).ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Master data")
            .WithName("SetZonePickRoute")
            .WithSummary("Choose the routing heuristic a zone's pick lists are sequenced with.")
            .Produces<ZoneResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<ZoneResponse>> HandleAsync(
        Guid id,
        SetZonePickRouteRequest request,
        MasterDataDbContext dbContext,
        ILocationDirectory locations,
        CancellationToken cancellationToken)
    {
        var zone = await dbContext.Zones
            .Include(z => z.Area)
            .SingleOrDefaultAsync(z => z.Id == id, cancellationToken);

        if (zone is null)
        {
            return Result.Failure<ZoneResponse>(MasterDataErrors.ZoneNotFound);
        }

        var code = request.Strategy?.Trim() ?? string.Empty;

        if (PickRouteCatalogue.Find(code) is not { } strategy)
        {
            return Result.Failure<ZoneResponse>(MasterDataErrors.UnknownRouteStrategy(code));
        }

        var map = await locations.FindZonePickRouteMapAsync([zone.Id], cancellationToken);

        // Against the geometry the zone will have after this save, not the one it has
        // now — otherwise turning on S-Shape and declaring the cross-aisle it needs
        // could never be done in one request, and doing it in two would mean passing
        // through a state the validator refuses.
        if (map.Geometry.Explain(zone.Id, strategy.Requires, request.HasRearCrossAisle) is { } missing)
        {
            return Result.Failure<ZoneResponse>(
                MasterDataErrors.RouteStrategyUnsupported(zone.Code, code, missing));
        }

        zone.PickRouteStrategy = strategy.Code;
        zone.HasRearCrossAisle = request.HasRearCrossAisle;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ZoneResponse(
            zone.Id,
            zone.Code,
            zone.Name,
            zone.Area!.Code,
            zone.TemperatureClass.ToString(),
            zone.PickSequence,
            zone.PickRouteStrategy,
            zone.HasRearCrossAisle,
            zone.IsActive,
            map.Geometry.AisleCount(zone.Id),
            map.Geometry.BayCount(zone.Id),
            GetZones.Options(map.Geometry, zone.Id, zone.HasRearCrossAisle));
    }
}
