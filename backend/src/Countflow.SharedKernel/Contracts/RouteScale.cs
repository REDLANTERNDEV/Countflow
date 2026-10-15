namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// Anything that has a place on the picker's route.
/// <para>
/// Extracted so that <see cref="RouteScale"/> is reusable rather than merely
/// referenced. The arithmetic below has been got wrong five times, and every one of
/// those was somebody re-deriving <c>zone × 1,000,000 + position</c> beside the type
/// that exists to own it. A caller that has a route position and not a
/// <see cref="StorageLocationSnapshot"/> now has somewhere to stand.
/// </para>
/// </summary>
public interface IRoutePosition
{
    /// <summary>Walking order of the zone. Ranked, never measured — see <see cref="RouteScale"/>.</summary>
    int ZonePickSequence { get; }

    /// <summary>Walking order within the zone.</summary>
    int PickSequence { get; }
}

/// <summary>
/// Turns a location's place on the picker's route into a number between 0 and 1.
/// <para>
/// <b>Why this is a type and not two lines in a context.</b> Every vocabulary has a
/// travel preference, all of them have to answer "how far along the building is
/// this?", and the arithmetic has now been got wrong four times. Three of those are
/// on the record — allocation's travel rule and FEFO in Phase 4, counting's travel
/// rule in Phase 5 — and the fourth was found by Phase 6's own influence report the
/// first time it was pointed at a simulated warehouse, which is the strongest thing
/// that can be said for building the report at all.
/// </para>
/// <para>
/// <b>The fourth one, and why the obvious composite does not work.</b> A route
/// position is naturally <c>zone × 1,000,000 + position</c>: zones sort ahead of
/// the locations inside them, because a picker who crosses between zones twice has
/// walked the building twice. Normalising that by dividing it by the building's
/// largest composite looks right and is not. Zone pick sequences are sparse and
/// arbitrary — this warehouse numbers them 0, 10, 20, 90, 99, 100 — so the largest
/// composite is a hundred million, every bin in the ambient hall lands within a
/// thousand of ten million, and the difference between the nearest aisle and the
/// furthest comes out at one part in a hundred thousand. Travel was weighted 0.25
/// of the putaway rule set and realised 0.8% of the ranking. It was not breaking
/// ties; it could not express one.
/// </para>
/// <para>
/// So a zone is ranked rather than measured. Its <i>order</i> is what carries
/// meaning — first zone, second zone — and its pick sequence is an arbitrary label
/// that happens to sort. Each zone then gets an equal slice of <c>[0, 1]</c> and
/// the positions inside it spread across that slice, so both the zone and the bin
/// matter, in that order, which is what the composite was always trying to say.
/// </para>
/// </summary>
public sealed record RouteScale
{
    private readonly IReadOnlyDictionary<int, int> _zoneRanks;
    private readonly long _slice;
    private readonly long _nearest;
    private readonly long _span;

    private RouteScale(IReadOnlyDictionary<int, int> zoneRanks, long slice, long nearest, long span)
    {
        _zoneRanks = zoneRanks;
        _slice = slice;
        _nearest = nearest;
        _span = span;
    }

    /// <summary>A scale with nothing on it. Every location scores 0 — at the front, and equally so.</summary>
    public static readonly RouteScale Empty = new(new Dictionary<int, int>(), 1L, 0L, 0L);

    /// <summary>
    /// Builds the scale from the locations it has to measure between.
    /// <para>
    /// Which locations those are is the question three phases have got wrong in one
    /// direction or the other, and it is not a detail of this type — it is the
    /// caller's judgement. Putaway and counting pass the whole warehouse, because
    /// they are choosing among a building. Allocation passes the handful of holdings
    /// one SKU is in, because four bins in one aisle measured against a twenty-four
    /// aisle hall score identically and say nothing. The question to ask is whether
    /// the list <i>is</i> the thing being chosen among, or merely what survived the
    /// choosing.
    /// </para>
    /// </summary>
    public static RouteScale Of(IEnumerable<IRoutePosition> locations)
    {
        var known = locations.ToList();

        if (known.Count == 0)
        {
            return Empty;
        }

        var zoneRanks = known
            .Select(location => location.ZonePickSequence)
            .Distinct()
            .Order()
            .Select((sequence, rank) => (sequence, rank))
            .ToDictionary(entry => entry.sequence, entry => entry.rank);

        // One more than the furthest position inside any zone, so a zone's slice is
        // wide enough to hold every position in it and no position spills into the
        // next zone's.
        var slice = known.Max(location => (long)location.PickSequence) + 1L;

        // Measured between the ends the scale actually has, not between zero and the
        // largest position it could theoretically hold.
        //
        // This is the fifth instance of the failure this type was extracted to
        // prevent, and it hid inside the fix for the fourth. Dividing a position by
        // the largest position makes a scale built from the whole building behave —
        // there is always something at the front, so the nearest really is near
        // zero — and makes a scale built from a *handful of candidates* useless, in
        // exactly the way the old composite was useless: four holdings deep in the
        // ambient hall all divide out to 0.9998-something, and the scale that was
        // supposed to say "this one is nearest" cannot express the difference. It
        // showed up as a candidate field spread of 0.0016 where the type's own
        // documentation promises the nearest scores 0 and the furthest scores 1.
        var positions = known.Select(Position).ToList();

        var nearest = positions.Min();
        var span = positions.Max() - nearest;

        return new RouteScale(zoneRanks, slice, nearest, span);

        long Position(IRoutePosition location) =>
            (zoneRanks.GetValueOrDefault(location.ZonePickSequence, zoneRanks.Count) * slice)
            + location.PickSequence;
    }

    /// <summary>
    /// Where the location sits along the route, on <c>[0, 1]</c>. The front of the
    /// scale is 0 and the far end is 1.
    /// <para>
    /// A location the scale was not built from is clamped rather than refused: it is
    /// off the end of the field, which is a real answer, and a rule that threw here
    /// would take a whole replay down over one bin in a zone nobody counted.
    /// </para>
    /// </summary>
    /// <returns>
    /// Zero when the scale cannot discriminate — one zone with one position, or no
    /// locations at all. <b>Zero and not one</b>: a lone candidate reported as
    /// "100% of the way to the far end of the warehouse" while contributing nothing
    /// is how people stop believing the numbers, and it is the degenerate case Phase
    /// 4 had to fix in allocation.
    /// </returns>
    public decimal Normalise(IRoutePosition location)
    {
        if (_span <= 0L)
        {
            return 0m;
        }

        var rank = _zoneRanks.TryGetValue(location.ZonePickSequence, out var known)
            ? known
            : _zoneRanks.Count;

        var position = (rank * _slice) + location.PickSequence;

        return Math.Clamp((decimal)(position - _nearest) / _span, 0m, 1m);
    }

    /// <summary>True when the scale has more than one place on it, and can therefore rank anything.</summary>
    public bool Discriminates => _span > 0L;
}
