namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// What a location currently holds, from the point of view of one item that is
/// looking for somewhere to go.
/// <para>
/// Framed around a subject item rather than as a neutral dump of contents,
/// because that is the question every slotting rule asks: is my item already
/// here, is a <i>different</i> batch of it here, and is there room. Answering it
/// as one shaped query keeps a putaway decision to a single round trip instead of
/// one per candidate bin.
/// </para>
/// </summary>
/// <param name="OccupiedVolumeM3">
/// Null when any item present has no dimensions on file. Unknown is not zero: a
/// capacity rule that treated missing master data as "empty" would cheerfully
/// slot a pallet into a full bay, which is how bad master data turns into
/// physical damage.
/// </param>
public sealed record LocationOccupancy(
    Guid LocationId,
    int DistinctItems,
    decimal TotalUnits,
    decimal? OccupiedVolumeM3,
    decimal? OccupiedWeightKg,
    decimal SubjectItemUnits,
    bool HoldsSubjectLot,
    bool HoldsOtherLotOfSubjectItem)
{
    /// <summary>
    /// Folds raw holdings into per-location occupancy.
    /// <para>
    /// Published here rather than kept inside Inventory because Phase 6 gave it a
    /// second caller: shadow replay describes the warehouse as it stood at some
    /// past instant, from the ledger, and has to describe it in exactly the terms
    /// the live query would have used. Two implementations of this fold would be
    /// two ways to answer "how full was that bay", and a replay that disagreed
    /// with the decision it is auditing would be worse than no replay — it would
    /// report differences the rule change did not cause.
    /// </para>
    /// </summary>
    /// <param name="itemsById">
    /// Dimensions for every item present. Items missing from this map poison their
    /// location's volume and weight to null, which is the honest answer: a partial
    /// sum understates how full a bay is, and understating it is the direction
    /// that breaks racking.
    /// </param>
    public static IReadOnlyDictionary<Guid, LocationOccupancy> Summarise(
        IEnumerable<OccupancyLine> lines,
        IReadOnlyDictionary<Guid, ItemSnapshot> itemsById,
        Guid subjectItemId,
        Guid? subjectLotId)
    {
        return lines
            .GroupBy(line => line.LocationId)
            .ToDictionary(
                group => group.Key,
                group => Fold(group.Key, [.. group], itemsById, subjectItemId, subjectLotId));
    }

    private static LocationOccupancy Fold(
        Guid locationId,
        IReadOnlyList<OccupancyLine> held,
        IReadOnlyDictionary<Guid, ItemSnapshot> itemsById,
        Guid subjectItemId,
        Guid? subjectLotId)
    {
        decimal? volume = 0m;
        decimal? weight = 0m;

        foreach (var line in held)
        {
            var item = itemsById.GetValueOrDefault(line.ItemId);

            volume = volume is null || item?.UnitVolumeM3 is not { } unitVolume
                ? null
                : volume + (unitVolume * line.Quantity);

            weight = weight is null || item?.UnitWeightKg is not { } unitWeight
                ? null
                : weight + (unitWeight * line.Quantity);
        }

        var subject = held.Where(line => line.ItemId == subjectItemId).ToList();

        return new LocationOccupancy(
            locationId,
            DistinctItems: held.Select(line => line.ItemId).Distinct().Count(),
            TotalUnits: held.Sum(line => line.Quantity),
            OccupiedVolumeM3: volume,
            OccupiedWeightKg: weight,
            SubjectItemUnits: subject.Sum(line => line.Quantity),
            HoldsSubjectLot: subject.Any(line => line.LotId == subjectLotId),
            HoldsOtherLotOfSubjectItem: subject.Any(line => line.LotId != subjectLotId));
    }
}

/// <summary>One holding, reduced to what an occupancy fold needs of it.</summary>
public sealed record OccupancyLine(Guid LocationId, Guid ItemId, Guid? LotId, decimal Quantity);

/// <summary>One item and lot sitting on a licence plate.</summary>
public sealed record PlateLine(Guid ItemId, Guid? LotId, StockCondition Condition, decimal Quantity);

/// <summary>
/// One item and lot on a licence plate, together with where it is sitting.
/// <para>
/// The location travels with the line because <c>LicensePlate</c> deliberately has
/// no <c>CurrentLocationId</c> (ADR 0001) — where a plate is, is where its
/// balances are. A caller that already knows the location wants
/// <see cref="PlateLine"/> and the cheaper query behind it; a caller that has only
/// an SSCC in its hand, which is every reprint, has to be told.
/// </para>
/// <para>
/// A plate genuinely can be in two places at once here, and the shape admits it
/// rather than picking one: a pallet broken down across two bays is several
/// holdings under one licence plate, and a label that silently named the first of
/// them would be a label asserting something the ledger does not.
/// </para>
/// </summary>
public sealed record PlateHolding(
    Guid ItemId,
    Guid? LotId,
    Guid LocationId,
    StockCondition Condition,
    decimal Quantity);

/// <summary>
/// A batch, as another module needs to display it.
/// <para>
/// Lots live in Inventory because an expiry date belongs to stock rather than to
/// a catalogue entry. A task screen still has to print the batch number the
/// operator is looking at on the carton, so the identifier crosses the boundary
/// through here rather than through a join nobody is allowed to write.
/// </para>
/// </summary>
public sealed record LotSnapshot(Guid Id, Guid ItemId, string LotNumber, DateOnly? ExpiryDate);

/// <summary>
/// Inventory's published read surface for anyone deciding where stock should go.
/// <para>
/// Separate from the enquiry endpoints, which serve people. This serves rules,
/// and it is shaped for them.
/// </para>
/// </summary>
public interface IStockQuery
{
    /// <summary>
    /// Occupancy for the given locations. Locations that hold nothing are absent
    /// from the result rather than present and empty — an empty warehouse would
    /// otherwise pay to transmit two thousand rows of zero.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, LocationOccupancy>> OccupancyAsync(
        IReadOnlyCollection<Guid> locationIds,
        Guid subjectItemId,
        Guid? subjectLotId,
        CancellationToken cancellationToken);

    /// <summary>
    /// What is on a licence plate at a given location. The whole-plate move reads
    /// this to work out what it is actually moving.
    /// </summary>
    Task<IReadOnlyList<PlateLine>> PlateContentsAsync(
        Guid licensePlateId,
        Guid locationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Everything posted against a licence plate, wherever it is. Empty for a
    /// plate that has been labelled and not yet filled — the ordinary state of a
    /// pallet between the label coming off the printer and the first case going on
    /// it, and the reason this returns a list rather than refusing.
    /// </summary>
    Task<IReadOnlyList<PlateHolding>> PlateHoldingsAsync(
        Guid licensePlateId,
        CancellationToken cancellationToken);

    /// <summary>Resolves a scanned SSCC to a licence plate.</summary>
    Task<Guid?> FindLicensePlateAsync(string sscc, CancellationToken cancellationToken);

    /// <summary>Batch lookup for display. Absent ids are simply missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, LotSnapshot>> FindLotsAsync(
        IReadOnlyCollection<Guid> lotIds,
        CancellationToken cancellationToken);

    /// <summary>Licence plate ids to the SSCCs printed on their labels.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindLicensePlateLabelsAsync(
        IReadOnlyCollection<Guid> licensePlateIds,
        CancellationToken cancellationToken);
}
