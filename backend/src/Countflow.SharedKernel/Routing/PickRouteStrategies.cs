namespace Countflow.SharedKernel.Routing;

/// <summary>
/// The order the bins are numbered in, and nothing cleverer.
/// <para>
/// <b>This is not a straw man, and it is not free.</b> The seeded warehouse already
/// bakes a serpentine into <c>Location.PickSequence</c> — even aisles are numbered
/// backwards — so this ordering <i>is</i> an S-Shape for as long as no aisle is
/// skipped. It is the honest default for a zone whose bins record no aisle and no
/// bay, which is every zone in a building nobody has surveyed, and it is a large
/// improvement on what the queue does today: picks are raised one per commitment
/// inside one release and handed out by <c>Priority, CreatedAt</c>, so the order a
/// picker actually walks is the order the customer typed their order lines in.
/// </para>
/// <para>
/// Broken ties on the code deliberately. A bay's two positions share a pick
/// sequence — position is not in the composite — so without a second key the list
/// is non-deterministic between two runs over the same set, which makes a tour
/// impossible to reproduce and a test impossible to write.
/// </para>
/// </summary>
public sealed class SequenceOrdinalRoute : IPickRouteStrategy
{
    public string Code => PickRouteCatalogue.SequenceOrdinal;

    public RouteGeometryRequirement Requires => RouteGeometryRequirement.None;

    public IReadOnlyList<RouteStop> Sequence(
        IReadOnlyList<RouteStop> stops,
        Guid zoneId,
        RouteGeometry geometry) =>
        [.. stops.OrderBy(s => s.PickSequence).ThenBy(s => s.Code, StringComparer.Ordinal)];
}

/// <summary>
/// Down one aisle and back up the next: every aisle holding a pick is traversed
/// end to end, entering alternately from the front and the rear.
/// <para>
/// <b>The alternation counts aisles visited, not aisles numbered, and that is the
/// entire difference between this and a sort.</b> A picker who leaves aisle 2 at the
/// far end and finds nothing to pick in aisle 3 arrives at aisle 4 still at the far
/// end, and must walk it back to front. Ordering by the seeded serpentine sends them
/// to aisle 4's first bay instead — a full aisle-length wrong, every time an odd
/// number of aisles is skipped, which on a picking wave is most of them.
/// </para>
/// <para>
/// Needs a rear cross-aisle, because leaving an aisle at the far end is the one
/// thing it does. A hall without one is refused this strategy at configuration time
/// rather than walked into a wall.
/// </para>
/// </summary>
public sealed class SShapeRoute : IPickRouteStrategy
{
    public string Code => PickRouteCatalogue.SShape;

    public RouteGeometryRequirement Requires => new(Aisles: true, Bays: true, RearCrossAisle: true);

    public IReadOnlyList<RouteStop> Sequence(
        IReadOnlyList<RouteStop> stops,
        Guid zoneId,
        RouteGeometry geometry)
    {
        var (placeable, orphans) = PickRoute.Split(stops, geometry);

        var ordered = new List<RouteStop>(stops.Count);
        var towardsTheRear = true;

        foreach (var aisle in placeable
                     .GroupBy(s => geometry.AisleRank(s)!.Value)
                     .OrderBy(group => group.Key))
        {
            var run = towardsTheRear
                ? aisle.OrderBy(s => geometry.BayDepth(s)!.Value).ThenBy(s => s.Code, StringComparer.Ordinal)
                : aisle.OrderByDescending(s => geometry.BayDepth(s)!.Value)
                    .ThenBy(s => s.Code, StringComparer.Ordinal);

            ordered.AddRange(run);

            // Flipped once per aisle that had something in it. Flipping on the
            // aisle's number instead is the bug this class exists to not have.
            towardsTheRear = !towardsTheRear;
        }

        ordered.AddRange(orphans);

        return ordered;
    }
}

/// <summary>
/// Into each aisle as far as the deepest pick, and back out the way you came in.
/// <para>
/// The strategy for a hall with one cross-aisle, which is most small halls and every
/// pick module built against a wall. It needs no rear cross-aisle, which is the whole
/// reason it exists, and it is what a warehouse should be running when it cannot
/// honestly claim to have one.
/// </para>
/// <para>
/// The walk back out is travel, not a stop, so what this produces is bins ordered
/// aisle by aisle and shallow to deep. That it comes out looking like a plain sort
/// of two ordinals is not a sign it is trivial — it is the sequence that a Return
/// walk visits, and it differs from <see cref="SequenceOrdinalRoute"/> on this
/// warehouse for every even-numbered aisle, because the seeded pick sequence numbers
/// those backwards for a serpentine this strategy is not walking.
/// </para>
/// </summary>
public sealed class ReturnRoute : IPickRouteStrategy
{
    public string Code => PickRouteCatalogue.Return;

    public RouteGeometryRequirement Requires => new(Aisles: true, Bays: true, RearCrossAisle: false);

    public IReadOnlyList<RouteStop> Sequence(
        IReadOnlyList<RouteStop> stops,
        Guid zoneId,
        RouteGeometry geometry)
    {
        var (placeable, orphans) = PickRoute.Split(stops, geometry);

        return
        [
            .. placeable
                .OrderBy(s => geometry.AisleRank(s)!.Value)
                .ThenBy(s => geometry.BayDepth(s)!.Value)
                .ThenBy(s => s.Code, StringComparer.Ordinal),
            .. orphans
        ];
    }
}

/// <summary>
/// The front halves of the aisles on the way out, the back halves on the way home.
/// <para>
/// Each aisle is split at its midpoint. Picks in the front half are reached from the
/// front cross-aisle as the picker works up the rack, and picks in the back half from
/// the rear cross-aisle as they work back down it, so no aisle is ever traversed in
/// full. It beats S-Shape when picks are sparse — traversing a whole aisle for one
/// carton at its mouth is the failure S-Shape has — and loses to it when they are
/// dense. Which of those a warehouse is, is exactly the thing a supervisor knows and
/// the system cannot infer, and is why this is a per-zone setting rather than a
/// judgement.
/// </para>
/// <para>
/// The midpoint is the middle of the <i>aisle</i>, not the middle of the picks in it.
/// That is why <see cref="RouteGeometry"/> is built from every bin in the zone rather
/// than from the stops on the tour: a grid ranked over two picks in bays 05 and 09
/// would put the second one at the back of a rack it is halfway down.
/// </para>
/// </summary>
public sealed class MidpointRoute : IPickRouteStrategy
{
    public string Code => PickRouteCatalogue.Midpoint;

    public RouteGeometryRequirement Requires => new(Aisles: true, Bays: true, RearCrossAisle: true);

    public IReadOnlyList<RouteStop> Sequence(
        IReadOnlyList<RouteStop> stops,
        Guid zoneId,
        RouteGeometry geometry)
    {
        var (placeable, orphans) = PickRoute.Split(stops, geometry);

        var midpoint = geometry.BayCount(zoneId) / 2;

        var outbound = placeable
            .Where(s => geometry.BayDepth(s)!.Value < midpoint)
            .OrderBy(s => geometry.AisleRank(s)!.Value)
            .ThenBy(s => geometry.BayDepth(s)!.Value)
            .ThenBy(s => s.Code, StringComparer.Ordinal);

        // Back down the rear cross-aisle, so the aisles come in reverse and each is
        // entered from its far end.
        var homeward = placeable
            .Where(s => geometry.BayDepth(s)!.Value >= midpoint)
            .OrderByDescending(s => geometry.AisleRank(s)!.Value)
            .ThenByDescending(s => geometry.BayDepth(s)!.Value)
            .ThenBy(s => s.Code, StringComparer.Ordinal);

        return [.. outbound, .. homeward, .. orphans];
    }
}
