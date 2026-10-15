namespace Countflow.SharedKernel.Routing;

/// <summary>
/// Everything a sequencer is allowed to know about the building, gathered in one
/// read.
/// <para>
/// The same discipline the rule contexts follow: every fetch happens once, before
/// any ordering starts, so the strategies stay pure functions and can be tested
/// without a database. It is a third published shape rather than four more fields on
/// <c>StorageLocationSnapshot</c>, because that contract already warns in its own
/// summary that widening it to satisfy a hungrier caller is how a published contract
/// turns back into a shared schema — and routing wants two facts (aisle, bay) that no
/// rule has ever needed and two (the zone's strategy, its cross-aisle) that are not
/// about a location at all.
/// </para>
/// </summary>
/// <param name="Stops">The locations that were asked about, by id. Missing ids are locations that are not places.</param>
/// <param name="Zones">How each zone concerned is configured to be walked.</param>
/// <param name="Geometry">The full aisle grid of those zones, not merely of the stops.</param>
public sealed record PickRouteMap(
    IReadOnlyDictionary<Guid, RouteStop> Stops,
    IReadOnlyDictionary<Guid, ZoneRouting> Zones,
    RouteGeometry Geometry)
{
    public static readonly PickRouteMap Empty = new(
        new Dictionary<Guid, RouteStop>(),
        new Dictionary<Guid, ZoneRouting>(),
        RouteGeometry.Empty);

    /// <summary>
    /// Plans the walk for the given locations, in the order they were handed over.
    /// <para>
    /// A location this map does not know is dropped from the walk rather than placed
    /// at a guessed position — but only from the <i>walk</i>. The caller still holds
    /// its own list and is responsible for not losing the row; see how
    /// <c>ClaimNextTour</c> appends what the map could not place.
    /// </para>
    /// </summary>
    public PickRoutePlan Plan(IEnumerable<Guid> locationIds)
    {
        var stops = locationIds
            .Select(id => Stops.GetValueOrDefault(id))
            .OfType<RouteStop>()
            .ToList();

        return PickRoute.Plan(stops, Zones, Geometry);
    }
}
