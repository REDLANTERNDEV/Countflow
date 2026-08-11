using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// Everything the putaway rules are allowed to know, gathered once before
/// evaluation starts.
/// <para>
/// The engine is a pure function (see <c>RuleEngine</c>), so every fetch happens
/// here and none happens inside a rule. That is not tidiness: it is what lets
/// Phase 6 replay a candidate rule set over thousands of historical decisions
/// without a database in the loop, and what stops a rule added in a hurry from
/// issuing a query per candidate bin.
/// </para>
/// </summary>
/// <param name="RequiredVolumeM3">
/// What this putaway needs. Null when the item has no dimensions on file — and
/// the capacity rules treat that as "cannot tell", not as "needs nothing".
/// </param>
/// <param name="FurthestPickSequence">
/// The largest route position in the warehouse, used to normalise travel onto
/// <c>[0, 1]</c> so a preference weight means the same thing in a six-aisle
/// chiller as in a twenty-four-aisle ambient hall.
/// </param>
public sealed record PutawayContext(
    ItemSnapshot Item,
    Guid? LotId,
    decimal Quantity,
    StockCondition Condition,
    Guid OriginLocationId,
    IReadOnlyDictionary<Guid, LocationOccupancy> Occupancy,
    decimal? RequiredVolumeM3,
    decimal? RequiredWeightKg,
    long FurthestPickSequence)
{
    /// <summary>What the candidate currently holds, or null when it is empty.</summary>
    public LocationOccupancy? At(Guid locationId) => Occupancy.GetValueOrDefault(locationId);

    /// <summary>
    /// Route position normalised onto <c>[0, 1]</c>, zone first. Zones sort ahead
    /// of locations within them for the same reason pick lists do: a picker who
    /// crosses between zones twice has walked the building twice.
    /// </summary>
    public decimal NormalisedDistance(StorageLocationSnapshot location)
    {
        if (FurthestPickSequence <= 0)
        {
            return 0m;
        }

        var position = ((long)location.ZonePickSequence * 1_000_000) + location.PickSequence;

        return Math.Clamp((decimal)position / FurthestPickSequence, 0m, 1m);
    }
}
