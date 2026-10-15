namespace Countflow.SharedKernel.Routing;

/// <summary>
/// What one zone's worth of the walk was asked to do and what it actually did.
/// </summary>
/// <param name="FellBackBecause">
/// Null when the zone was walked the way it is configured to be. A sentence when it
/// was not — an unregistered code, or geometry the strategy needs and the zone does
/// not have. Reported rather than thrown, and stamped on the tour, because the
/// alternative is a picker at the start of a shift being told the warehouse is
/// misconfigured and handed no work.
/// </param>
public sealed record PickRouteSegment(
    Guid? ZoneId,
    string? ZoneCode,
    string RequestedStrategy,
    string AppliedStrategy,
    string? FellBackBecause,
    int Stops);

/// <summary>
/// A walk, and the arithmetic that says whether it was worth computing.
/// </summary>
/// <param name="Baseline">
/// What the same stops would have cost in the order they arrived — which is the
/// order the queue would have handed them out in, which is order-line order. This is
/// the instrument, and it is the reason the cost model exists at all: a heuristic
/// nobody can check is a heuristic that will still be running years after it stopped
/// helping. It is the same argument <c>RuleInfluence</c> was built on, arriving at
/// the one part of the system that produces no ranking for it to read.
/// </param>
public sealed record PickRoutePlan(
    IReadOnlyList<RouteStop> Stops,
    IReadOnlyList<PickRouteSegment> Segments,
    RouteTravel Travel,
    RouteTravel Baseline)
{
    /// <summary>
    /// Steps saved against working the stops in the order they were raised, or null
    /// when the geometry could not measure one of the two walks. Negative is a real
    /// answer and is not clamped: a strategy that costs more than doing nothing is
    /// the finding, and hiding it behind a zero is how a bad setting survives.
    /// </summary>
    public int? StepsSaved => Travel.IsMeasured && Baseline.IsMeasured
        ? Baseline.Steps - Travel.Steps
        : null;

    /// <summary>Every zone that did not get the strategy it asked for.</summary>
    public IEnumerable<PickRouteSegment> Fallbacks => Segments.Where(s => s.FellBackBecause is not null);
}

/// <summary>
/// Turns a set of stops into the order a picker walks them.
/// <para>
/// <b>Zone first, always.</b> The stops are cut into zone segments in zone order and
/// each segment is walked with its own zone's strategy, which is what "pluggable per
/// zone" has to mean once a tour can span two of them. It is also the property
/// <c>Zone.PickSequence</c> has promised since Phase 1 — that a pick list never sends
/// somebody back and forth between zones — and the one place where the thing to reuse
/// really is the ordinal zone ranking <see cref="Contracts.RouteScale"/> is built on,
/// rather than the scalar it projects that ranking onto.
/// </para>
/// <para>
/// <b>Nothing here is a decision, and it does not carry a trace.</b> A trace exists
/// because a judgement is not recomputable — the field has moved on and the rule set
/// has been promoted, so the reasoning has to be stored or it is gone. This is a pure
/// function of a stop set, a strategy and a grid, all three of which are stored, so a
/// trace would persist what recomputing already answers. What it does carry is a
/// stamp of which strategy produced it, for the reason a <c>LabelPrint</c> stamps its
/// template: the setting will have moved on, and somebody is holding the pick list.
/// See <c>decision 15</c>.
/// </para>
/// </summary>
public static class PickRoute
{
    /// <summary>
    /// Plans the walk. <paramref name="stops"/> arrive in the order they would
    /// otherwise have been worked, which is what makes
    /// <see cref="PickRoutePlan.Baseline"/> mean anything.
    /// </summary>
    public static PickRoutePlan Plan(
        IReadOnlyList<RouteStop> stops,
        IReadOnlyDictionary<Guid, ZoneRouting> zones,
        RouteGeometry geometry)
    {
        if (stops.Count == 0)
        {
            return new PickRoutePlan([], [], RouteTravel.Unknown, RouteTravel.Unknown);
        }

        var ordered = new List<RouteStop>(stops.Count);
        var segments = new List<PickRouteSegment>();

        foreach (var group in stops
                     .GroupBy(stop => stop.ZoneId)
                     .OrderBy(group => group.Key is null ? 1 : 0)
                     .ThenBy(group => ZoneOrderOf(group.Key, zones))
                     .ThenBy(group => group.Key))
        {
            var zone = group.Key is { } id ? zones.GetValueOrDefault(id) : null;
            var members = group.ToList();

            var requested = zone?.StrategyCode ?? PickRouteCatalogue.Fallback.Code;

            var (strategy, because) = Resolve(requested, group.Key, geometry);

            ordered.AddRange(strategy.Sequence(members, group.Key ?? Guid.Empty, geometry));

            segments.Add(new PickRouteSegment(
                group.Key,
                zone?.ZoneCode,
                requested,
                strategy.Code,
                because,
                members.Count));
        }

        var applied = segments.Where(segment => segment.ZoneId is not null)
            .ToDictionary(segment => segment.ZoneId!.Value, segment => segment.AppliedStrategy);
        return new PickRoutePlan(ordered, segments, geometry.Travel(ordered, applied), geometry.Travel(stops));
    }

    /// <summary>
    /// Splits a zone's stops into the ones the grid can place and the ones it cannot.
    /// <para>
    /// Shared by every geometric strategy so that "a bin with no aisle recorded goes
    /// last, in pick-sequence order" is written once. It should be unreachable — a
    /// strategy needing aisles is not applied to a zone that has none — but a bin
    /// imported without an aisle into a zone that otherwise has them is one CSV away,
    /// and the strategy has to stay total. A dropped stop is a line that quietly does
    /// not get picked.
    /// </para>
    /// </summary>
    internal static (List<RouteStop> Placeable, List<RouteStop> Orphans) Split(
        IReadOnlyList<RouteStop> stops,
        RouteGeometry geometry)
    {
        var placeable = new List<RouteStop>(stops.Count);
        var orphans = new List<RouteStop>();

        foreach (var stop in stops)
        {
            if (geometry.AisleRank(stop) is not null && geometry.BayDepth(stop) is not null)
            {
                placeable.Add(stop);
            }
            else
            {
                orphans.Add(stop);
            }
        }

        orphans.Sort((left, right) =>
        {
            var bySequence = left.PickSequence.CompareTo(right.PickSequence);
            return bySequence != 0 ? bySequence : string.CompareOrdinal(left.Code, right.Code);
        });

        return (placeable, orphans);
    }

    private static (IPickRouteStrategy Strategy, string? Because) Resolve(
        string requested,
        Guid? zoneId,
        RouteGeometry geometry)
    {
        if (PickRouteCatalogue.Find(requested) is not { } strategy)
        {
            return (PickRouteCatalogue.Fallback, "route.unknown_strategy");
        }

        if (zoneId is not { } id)
        {
            // No zone means no grid and no configuration; the stops are ordered by
            // the only thing they carry. Not a fallback anybody needs telling about
            // unless they asked for something else.
            return strategy.Requires == RouteGeometryRequirement.None
                ? (strategy, null)
                : (PickRouteCatalogue.Fallback, "route.no_zone");
        }

        return geometry.Explain(id, strategy.Requires) is { } missing
            ? (PickRouteCatalogue.Fallback, missing)
            : (strategy, null);
    }

    private static int ZoneOrderOf(Guid? zoneId, IReadOnlyDictionary<Guid, ZoneRouting> zones) =>
        zoneId is { } id && zones.TryGetValue(id, out var zone) ? zone.PickSequence : int.MaxValue;
}
