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
    bool HoldsOtherLotOfSubjectItem);

/// <summary>One item and lot sitting on a licence plate.</summary>
public sealed record PlateLine(Guid ItemId, Guid? LotId, StockCondition Condition, decimal Quantity);

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
