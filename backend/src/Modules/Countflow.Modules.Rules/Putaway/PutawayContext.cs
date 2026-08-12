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
/// <param name="Route">
/// The building, as a scale to measure travel against. Built from every storage
/// location rather than from the admissible ones, because putaway is choosing among
/// two thousand bins and that is the field it is choosing from.
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
    RouteScale Route)
{
    /// <summary>What the candidate currently holds, or null when it is empty.</summary>
    public LocationOccupancy? At(Guid locationId) => Occupancy.GetValueOrDefault(locationId);

    /// <summary>
    /// Route position normalised onto <c>[0, 1]</c>, zone first. Zones sort ahead
    /// of locations within them for the same reason pick lists do: a picker who
    /// crosses between zones twice has walked the building twice.
    /// <para>
    /// The arithmetic lives in <see cref="RouteScale"/>, which is where the reason
    /// it is not the obvious composite is written down. The short version: dividing
    /// <c>zone × 1,000,000 + position</c> by the building's largest composite made
    /// every bin in the ambient hall score within one part in a hundred thousand of
    /// every other, so a rule weighted a quarter of the set realised under one per
    /// cent of the ranking. Phase 6's influence report found that the first time it
    /// was run.
    /// </para>
    /// </summary>
    public decimal NormalisedDistance(StorageLocationSnapshot location) => Route.Normalise(location);
}
