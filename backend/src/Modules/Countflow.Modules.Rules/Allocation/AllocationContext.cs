using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.Allocation;

/// <summary>
/// Everything the allocation rules are allowed to know, gathered once before
/// evaluation starts.
/// <para>
/// The same discipline as <c>PutawayContext</c> and for the same reason: the
/// engine is a pure function, so every fetch happens here and none happens inside
/// a rule. What is worth noticing is how little the two contexts have in common —
/// one is built around a warehouse full of empty bins, the other around a handful
/// of holdings and the dates on them — and that they still feed the same
/// evaluator.
/// </para>
/// </summary>
/// <param name="ShipOn">
/// The day the goods are expected to leave, defaulting to today. Shelf-life rules
/// measure from this rather than from now: an order picked on Monday for a Friday
/// trailer has four fewer days of life on it when the customer opens the box, and
/// the customer's contract is written about what they receive.
/// </param>
/// <param name="EarliestExpiry">
/// The expiry window across the candidates, used to normalise FEFO onto
/// <c>[0, 1]</c>. Normalising against the field rather than against a fixed
/// horizon is what makes a weight mean the same thing for milk and for tinned
/// tomatoes.
/// </param>
public sealed record AllocationContext(
    ItemSnapshot Item,
    decimal DemandQuantity,
    Guid? RequiredLotId,
    DateOnly ShipOn,
    IReadOnlyDictionary<Guid, StorageLocationSnapshot> Locations,
    DateOnly? EarliestExpiry,
    DateOnly? LatestExpiry,
    long NearestPickSequence,
    long FurthestPickSequence)
{
    public DateTimeOffset? EarliestReceipt { get; init; }
    public DateTimeOffset? LatestReceipt { get; init; }

    /// <summary>Known holding ages normalized across the field, like allocation's route window.</summary>
    public decimal ReceiptRotationScore(StockHolding holding)
    {
        if (holding.FirstReceivedAt is not { } received || EarliestReceipt is not { } first || LatestReceipt is not { } last) return 0.5m;
        var span = (last - first).Ticks;
        return span <= 0 ? 1m : Math.Clamp(1m - (decimal)(received - first).Ticks / span, 0m, 1m);
    }

    /// <summary>Where a holding is standing, or null when the location is not a storable place.</summary>
    public StorageLocationSnapshot? LocationOf(StockHolding holding) =>
        Locations.GetValueOrDefault(holding.LocationId);

    /// <summary>
    /// How much of the batch's life is still ahead of it on the shipping day, as a
    /// fraction. Null when the item has no declared shelf life or the batch has no
    /// expiry — in which case the rule that reads this admits, because "cannot
    /// tell" and "too old" are not the same answer.
    /// </summary>
    public decimal? RemainingShelfLifeFraction(StockHolding holding)
    {
        if (holding.ExpiryDate is not { } expiry || Item.ShelfLifeDays is not { } total || total <= 0)
        {
            return null;
        }

        var remaining = expiry.DayNumber - ShipOn.DayNumber;

        return Math.Clamp((decimal)remaining / total, 0m, 1m);
    }

    /// <summary>
    /// Rotation urgency on <c>[0, 1]</c>: how far through its life the batch is.
    /// <para>
    /// Measured against the item's own shelf life rather than against the other
    /// candidates, and that choice is load-bearing in two ways. It makes the score
    /// comparable between items — 80% through its life means the same thing for
    /// milk and for tinned tomatoes, which is what lets one weight govern both.
    /// And it cannot be distorted by stock that is not in the running: a window
    /// stretched between the earliest and latest candidate is anchored by every
    /// batch the constraints go on to refuse, so a single short-dated pallet
    /// nobody can ship would quietly flatten rotation's influence over the batches
    /// that can.
    /// </para>
    /// <para>
    /// It is also the truer statement. Two batches sixty days apart on a
    /// two-year life are nearly equally urgent, and a relative measure would
    /// report one as maximally urgent and the other as not urgent at all — which
    /// is how a pick-face preference ends up unable to break a tie it ought to
    /// break.
    /// </para>
    /// <para>
    /// Without a declared shelf life there is no absolute scale, so the field is
    /// the only comparison available and the window is used instead. A field where
    /// every candidate shares an expiry scores all of them 1: none is worse than
    /// another by rotation.
    /// </para>
    /// </summary>
    public decimal RotationScore(StockHolding holding)
    {
        if (RemainingShelfLifeFraction(holding) is { } remaining)
        {
            return Math.Clamp(1m - remaining, 0m, 1m);
        }

        if (holding.ExpiryDate is not { } expiry
            || EarliestExpiry is not { } earliest
            || LatestExpiry is not { } latest)
        {
            return 0.5m;
        }

        var span = latest.DayNumber - earliest.DayNumber;

        return span <= 0
            ? 1m
            : Math.Clamp(1m - ((decimal)(expiry.DayNumber - earliest.DayNumber) / span), 0m, 1m);
    }

    /// <summary>
    /// Route position normalised onto <c>[0, 1]</c> across the candidates, zone
    /// first — nearest is 0, furthest is 1.
    /// <para>
    /// Normalised against the <i>field</i> rather than against the whole
    /// warehouse, unlike putaway, and the difference is forced by what each
    /// decision is choosing between. Putaway compares two thousand bins, so the
    /// building is the scale. Allocation compares the four places a SKU happens to
    /// be, and measuring those against the length of the building would score them
    /// all at nearly the same distance and make the rule say nothing.
    /// </para>
    /// <para>
    /// A field where every holding sits at one position scores 0 — nearest —
    /// rather than 1. A single candidate is the closest one there is, and telling
    /// a supervisor it was "100% along the route" while contributing nothing to
    /// its own selection is the sort of trace that makes people distrust the
    /// scores.
    /// </para>
    /// </summary>
    public decimal NormalisedDistance(StorageLocationSnapshot location)
    {
        var span = FurthestPickSequence - NearestPickSequence;

        if (span <= 0)
        {
            return 0m;
        }

        var position = ((long)location.ZonePickSequence * 1_000_000) + location.PickSequence;

        return Math.Clamp((decimal)(position - NearestPickSequence) / span, 0m, 1m);
    }
}
