namespace Countflow.SharedKernel.Routing;

/// <summary>
/// One place a picker has to stand, with everything a routing heuristic is
/// allowed to know about where it is.
/// <para>
/// Deliberately not <c>StorageLocationSnapshot</c>. That contract already carries
/// fifteen fields and says in its own summary that widening it to satisfy a
/// hungrier caller is how a published contract turns back into a shared schema.
/// Routing needs four facts about a location and gets exactly four.
/// </para>
/// </summary>
public sealed record RouteStop(
    Guid LocationId,
    string Code,
    Guid? ZoneId,
    int ZonePickSequence,
    int PickSequence,
    string? Aisle,
    string? Bay) : Contracts.IRoutePosition;

/// <summary>
/// One cell of the aisle grid: a place in a zone that a bin occupies.
/// <para>
/// Distinct from <see cref="RouteStop"/> because the grid is built from the zone and
/// the walk is built from the stops — <see cref="MidpointRoute"/> is the one that
/// makes the difference load-bearing. It is also a much cheaper read: a zone's
/// distinct aisle-and-bay pairs are a couple of hundred rows where its bins are a
/// couple of thousand.
/// </para>
/// </summary>
public sealed record RouteCell(Guid ZoneId, string? Aisle, string? Bay);

/// <summary>
/// How a zone is walked, and whether it can be walked that way.
/// <para>
/// Both fields are reference data somebody types in once, and they sit beside
/// <c>TemperatureClass</c> for the same reason: they are facts about the physical
/// shape of a zone rather than tunable weights. See
/// <c>decision 15</c>.
/// </para>
/// </summary>
/// <param name="HasRearCrossAisle">
/// Whether a picker can leave an aisle at the far end instead of walking back out.
/// The single fact that separates the strategies that traverse from the strategies
/// that return, and the one nobody could have inferred from the data: the model
/// records where bins are, never how somebody gets between them.
/// </param>
public sealed record ZoneRouting(
    Guid ZoneId,
    string ZoneCode,
    int PickSequence,
    string StrategyCode,
    bool HasRearCrossAisle);

/// <summary>
/// What a strategy needs the building to know about itself before it can be used.
/// <para>
/// This is <c>decision 13</c>'s split in a different noun. A label kind is code
/// because it is a binding contract about which facts will be fetched; a routing
/// strategy is code because it is a binding contract about <i>which geometry it
/// requires</i>. Declaring it is what lets a zone be refused a strategy it cannot
/// support at the moment somebody configures it, rather than at six in the morning
/// with a picker waiting — the same trick as validating a rule set by building it
/// through the real catalogue.
/// </para>
/// </summary>
public sealed record RouteGeometryRequirement(bool Aisles, bool Bays, bool RearCrossAisle)
{
    /// <summary>Nothing beyond the pick sequence every location already has.</summary>
    public static readonly RouteGeometryRequirement None = new(false, false, false);
}

/// <summary>
/// What one ordering of the stops costs, in steps.
/// <para>
/// <b>Steps and not metres, and that is the whole design.</b> There are no
/// coordinates anywhere in this system — <c>Aisle</c>, <c>Bay</c>, <c>Level</c> and
/// <c>Position</c> are four nullable strings, documented as coordinates rather than
/// things — so "this walk is forty metres shorter" is not merely unrepresentable,
/// it is uncomputable. Shipping three named heuristics with no way to tell which
/// one is working would be shipping the thing this codebase has caught five scoring
/// flaws by refusing to ship.
/// </para>
/// <para>
/// So travel is <i>ranked, not measured</i>, which is exactly what
/// <see cref="Contracts.RouteScale"/> does to zones and for the same reason. One
/// step is one bay-length; crossing to the neighbouring aisle is
/// <see cref="RouteGeometry.AisleWidthInBays"/> of them. The number is a proxy and
/// is monotone in the real distance, which is all a comparison between two orderings
/// of the same stops needs. It is not a distance and must never be reported as one.
/// </para>
/// </summary>
/// <param name="IsMeasured">
/// False when some stop on the walk has no aisle or no bay recorded. The cost is
/// then not partially right, it is unknown, and it is published as null rather than
/// as a number somebody will believe. "We cannot tell you whether this helped" is
/// an answer; a figure computed over the half of the walk that had geometry is not.
/// </param>
public sealed record RouteTravel(int Steps, int AisleChanges, int ZoneChanges, bool IsMeasured)
{
    public static readonly RouteTravel Unknown = new(0, 0, 0, IsMeasured: false);
}

/// <summary>
/// The aisle grid of one or more zones, derived from the bins that are in them.
/// <para>
/// <b>Ranked, never parsed.</b> An aisle's ordinal is its position among the
/// distinct aisle codes in its zone, sorted ordinally — not the number pulled out of
/// "A07". Parsing invites a format ("A07" here, "7" there, "AISLE-7" at the next
/// site) and every parser needs a fallback that is silently wrong. Ranking needs
/// only that the codes sort in walking order, which zero-padded codes do, and it is
/// the move <see cref="Contracts.RouteScale"/> already made when it stopped
/// measuring zone pick sequences and started ranking them.
/// </para>
/// <para>
/// <b>Built from the zone, not from the stops.</b> Midpoint has to know whether a
/// pick is in the front half or the back half of an aisle, which is a fact about the
/// aisle's full extent. A grid ranked over only the bins a tour happens to visit
/// would put two picks in bays 05 and 09 at depths 0 and 1 and call the second one
/// the back of the rack.
/// </para>
/// </summary>
public sealed class RouteGeometry
{
    /// <summary>
    /// What it costs to walk from one aisle to the next one along, in bay-lengths.
    /// <para>
    /// A proxy, chosen because a pallet aisle is roughly two case-bays wide, and
    /// stated as a constant rather than a setting because a number nobody can
    /// calibrate is a number nobody should be invited to tune. It changes which of
    /// two orderings looks cheaper only when they are already within a step or two
    /// of each other.
    /// </para>
    /// </summary>
    public const int AisleWidthInBays = 2;

    /// <summary>
    /// The cost of crossing between zones. Large enough to dominate, because a
    /// picker who crosses between zones twice has walked the building twice — the
    /// same sentence <see cref="Contracts.RouteScale"/> is built on. Stops are
    /// segmented by zone before any strategy sees them, so this is the same for
    /// every ordering of a given set and cannot change which one wins; it is here so
    /// the reported figure is not a lie by omission.
    /// </summary>
    public const int ZoneChangeSteps = 100;

    private readonly Dictionary<(Guid Zone, string Aisle), int> _aisleRanks;
    private readonly Dictionary<(Guid Zone, string Bay), int> _bayDepths;
    private readonly Dictionary<Guid, int> _deepestBay;
    private readonly Dictionary<Guid, int> _aisleCount;
    private readonly HashSet<Guid> _rearCrossAisle;

    private RouteGeometry(
        Dictionary<(Guid, string), int> aisleRanks,
        Dictionary<(Guid, string), int> bayDepths,
        Dictionary<Guid, int> deepestBay,
        Dictionary<Guid, int> aisleCount,
        HashSet<Guid> rearCrossAisle)
    {
        _aisleRanks = aisleRanks;
        _bayDepths = bayDepths;
        _deepestBay = deepestBay;
        _aisleCount = aisleCount;
        _rearCrossAisle = rearCrossAisle;
    }

    /// <summary>A grid with nothing on it. Every strategy that needs geometry falls back on it.</summary>
    public static readonly RouteGeometry Empty = new(new(), new(), new(), new(), new());

    /// <summary>
    /// Builds the grid from every bin in the zones concerned, and from what those
    /// zones say about themselves.
    /// </summary>
    public static RouteGeometry Of(IEnumerable<RouteCell> zoneLocations, IEnumerable<ZoneRouting> zones)
    {
        var known = zoneLocations.ToList();

        var aisleRanks = known
            .Where(l => !string.IsNullOrWhiteSpace(l.Aisle))
            .GroupBy(l => l.ZoneId)
            .SelectMany(zone => zone
                .Select(l => l.Aisle!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select((aisle, rank) => ((zone.Key, aisle), rank)))
            .ToDictionary(entry => entry.Item1, entry => entry.rank);

        var bayDepths = known
            .Where(l => !string.IsNullOrWhiteSpace(l.Bay))
            .GroupBy(l => l.ZoneId)
            .SelectMany(zone => zone
                .Select(l => l.Bay!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select((bay, depth) => ((zone.Key, bay), depth)))
            .ToDictionary(entry => entry.Item1, entry => entry.depth);

        var deepest = bayDepths
            .GroupBy(entry => entry.Key.Item1)
            .ToDictionary(zone => zone.Key, zone => zone.Max(entry => entry.Value));

        var aisleCount = aisleRanks
            .GroupBy(entry => entry.Key.Item1)
            .ToDictionary(zone => zone.Key, zone => zone.Count());

        var rear = zones.Where(z => z.HasRearCrossAisle).Select(z => z.ZoneId).ToHashSet();

        return new RouteGeometry(aisleRanks, bayDepths, deepest, aisleCount, rear);
    }

    /// <summary>Where the aisle sits across its zone, or null when the bin records no aisle.</summary>
    public int? AisleRank(RouteStop stop) =>
        stop.ZoneId is { } zone
        && stop.Aisle is { } aisle
        && _aisleRanks.TryGetValue((zone, aisle), out var rank)
            ? rank
            : null;

    /// <summary>
    /// How far into the aisle the bin is, zero being the end nearest the front
    /// cross-aisle. Null when the bin records no bay.
    /// </summary>
    public int? BayDepth(RouteStop stop) =>
        stop.ZoneId is { } zone
        && stop.Bay is { } bay
        && _bayDepths.TryGetValue((zone, bay), out var depth)
            ? depth
            : null;

    /// <summary>The far end of the racking in this zone, in bay-lengths from the front.</summary>
    public int DeepestBay(Guid zoneId) => _deepestBay.GetValueOrDefault(zoneId);

    public int AisleCount(Guid zoneId) => _aisleCount.GetValueOrDefault(zoneId);

    public int BayCount(Guid zoneId) => _deepestBay.TryGetValue(zoneId, out var deepest) ? deepest + 1 : 0;

    public bool HasRearCrossAisle(Guid zoneId) => _rearCrossAisle.Contains(zoneId);

    /// <summary>
    /// Why the zone cannot be walked this way, or null when it can.
    /// <para>
    /// A sentence rather than a boolean, because this is read twice: once by a
    /// supervisor being told at configuration time that they cannot have S-Shape in
    /// a hall whose bins record no aisle, and once by whoever reads a tour that
    /// quietly fell back. Both need to know which of the three facts was missing.
    /// </para>
    /// </summary>
    /// <param name="rearCrossAisle">
    /// Overrides what the zone currently says about itself. The configuration screen
    /// sets the strategy and the cross-aisle in one move for exactly this reason:
    /// checking them separately makes the refusal depend on which half somebody
    /// happened to save first, and a supervisor who cannot turn on S-Shape until they
    /// have already declared a cross-aisle they have not yet been asked about will
    /// conclude the feature is broken.
    /// </param>
    public string? Explain(Guid zoneId, RouteGeometryRequirement requirement, bool? rearCrossAisle = null)
    {
        if (requirement.Aisles && AisleCount(zoneId) == 0)
        {
            return "route.no_aisles";
        }

        // Two, not one. A zone whose bins all share a bay code has an aisle with no
        // depth in it, and every heuristic that routes on depth would order such a
        // zone arbitrarily while claiming to have routed it.
        if (requirement.Bays && BayCount(zoneId) < 2)
        {
            return "route.no_bay_depth";
        }

        if (requirement.RearCrossAisle && !(rearCrossAisle ?? HasRearCrossAisle(zoneId)))
        {
            return "route.no_rear_cross_aisle";
        }

        return null;
    }

    /// <summary>
    /// What this ordering of the stops costs, in the units described on
    /// <see cref="RouteTravel"/>.
    /// <para>
    /// Measured between the stops and nowhere else. There is no depot in this model
    /// and no walk back to the bench, which would need a start location the queue
    /// does not have — and which is the same constant for every ordering of the same
    /// set anyway. What this number is for is comparing two orderings of one set;
    /// it is not an estimate of a shift.
    /// </para>
    /// </summary>
    public RouteTravel Travel(IReadOnlyList<RouteStop> ordered, IReadOnlyDictionary<Guid, string>? strategies = null)
    {
        if (ordered.Count < 2)
        {
            // One stop is no walk at all, and that is a measured zero rather than an
            // unknown: there is nothing between it and itself to fail to measure.
            return new RouteTravel(0, 0, 0, IsMeasured: true);
        }

        var steps = 0;
        var aisleChanges = 0;
        var zoneChanges = 0;
        var measured = true;
        var traversedAisles = new Dictionary<Guid, int>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var from = ordered[i - 1];
            var to = ordered[i];

            if (from.ZoneId != to.ZoneId)
            {
                zoneChanges++;
                steps += ZoneChangeSteps;
                continue;
            }

            if (to.ZoneId is not { } zone)
            {
                measured = false;
                continue;
            }

            if (AisleRank(from) is not { } fromAisle
                || AisleRank(to) is not { } toAisle
                || BayDepth(from) is not { } fromBay
                || BayDepth(to) is not { } toBay)
            {
                measured = false;
                continue;
            }

            if (fromAisle == toAisle)
            {
                steps += Math.Abs(fromBay - toBay);
                continue;
            }

            aisleChanges++;

            var crossing = Math.Abs(fromAisle - toAisle) * AisleWidthInBays;

            // Out to a cross-aisle, along it, and back in. The picker uses one
            // cross-aisle for both halves of that, so the ends are minimised
            // together rather than independently.
            var viaFront = fromBay + toBay;

            var deepest = DeepestBay(zone);
            var viaRear = HasRearCrossAisle(zone)
                ? (deepest - fromBay) + (deepest - toBay)
                : int.MaxValue;

            var strategy = strategies?.GetValueOrDefault(zone);
            var ordinal = traversedAisles.GetValueOrDefault(zone);
            var leg = strategy switch
            {
                PickRouteCatalogue.SShape => ordinal % 2 == 0 ? viaRear : viaFront,
                PickRouteCatalogue.Return => viaFront,
                PickRouteCatalogue.Midpoint => fromBay < BayCount(zone) / 2 && toBay < BayCount(zone) / 2
                    ? viaFront : viaRear,
                _ => Math.Min(viaFront, viaRear)
            };
            traversedAisles[zone] = ordinal + 1;
            steps += crossing + leg;
        }

        return new RouteTravel(steps, aisleChanges, zoneChanges, measured);
    }
}
