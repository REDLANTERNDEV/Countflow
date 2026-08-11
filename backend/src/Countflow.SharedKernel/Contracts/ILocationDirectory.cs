namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// Which side of the warehouse boundary a virtual location represents.
/// <para>
/// The full <c>LocationType</c> vocabulary belongs to MasterData and stays
/// there. What the ledger needs to know is narrower: whether a location is a
/// counterparty rather than a shelf, and which counterparty, so it can insist a
/// receipt comes from Supplier and a despatch goes to Customer. Publishing this
/// coarse enum instead of the whole topology keeps the contract honest — a
/// consumer cannot start reasoning about pick faces through the back door.
/// </para>
/// </summary>
public enum CounterpartyKind
{
    /// <summary>A real, physical location inside the building.</summary>
    None = 0,

    Supplier = 1,
    Customer = 2,
    Adjustment = 3,
    Scrap = 4,
    InTransit = 5
}

/// <summary>
/// What another module is allowed to know about a location.
/// <para>
/// Deliberately a flat snapshot rather than the <c>Location</c> entity: handing
/// out a tracked entity from another module's DbContext would let the consumer
/// mutate it, which is the boundary violation the interface exists to prevent.
/// </para>
/// </summary>
public sealed record LocationSnapshot(
    Guid Id,
    Guid WarehouseId,
    string Code,
    CounterpartyKind Counterparty,
    bool IsActive,
    bool CanHoldAvailableStock)
{
    /// <summary>
    /// Virtual locations are the contra accounts of the double-entry ledger
    /// (ADR 0001). They may go negative; a shelf may not.
    /// </summary>
    public bool IsVirtual => Counterparty != CounterpartyKind.None;
}

/// <summary>
/// What a location is <i>for</i>, coarsely enough to slot stock into it.
/// <para>
/// Another deliberate narrowing, like <see cref="CounterpartyKind"/>: putaway
/// has to tell reserve racking from a forward pick face, because the two are
/// filled for opposite reasons, but it has no business knowing that MasterData
/// also distinguishes a pack bench from a shipping door.
/// </para>
/// </summary>
public enum StorageRole
{
    /// <summary>Not a place stock is stored at all — a virtual counterparty.</summary>
    None = 0,

    /// <summary>Reserve racking. Pallets in, replenishment out.</summary>
    Reserve = 1,

    /// <summary>Forward pick face. Small, fast, replenished from reserve.</summary>
    Forward = 2,

    /// <summary>Marshalling space. Holds stock, but only in passing.</summary>
    Staging = 3,

    /// <summary>Held stock — quarantine, inspection, recall.</summary>
    Hold = 4,

    /// <summary>A door. Where goods enter and leave, and the only place a receipt may land.</summary>
    Dock = 5,

    /// <summary>A pack bench. Work happens on it; nothing is stored there.</summary>
    Bench = 6
}

/// <summary>
/// A physical location with the attributes a slotting decision needs.
/// <para>
/// Separate from <see cref="LocationSnapshot"/> rather than folded into it. The
/// ledger needs three facts about a location and gets exactly three; the rules
/// engine needs its shape, its capacity and where it sits on a picker's route.
/// Widening the common snapshot to satisfy the hungrier caller would hand every
/// consumer the whole topology, which is how a published contract quietly turns
/// back into a shared schema.
/// </para>
/// </summary>
/// <param name="ZonePickSequence">
/// Walking order of the zone, which sorts ahead of <paramref name="PickSequence"/>
/// within it. Travel is over half of warehouse labour, so both numbers are what
/// a travel-aware rule actually scores on.
/// </param>
public sealed record StorageLocationSnapshot(
    Guid Id,
    Guid WarehouseId,
    string Code,
    Guid? ZoneId,
    string? ZoneCode,
    TemperatureBand Temperature,
    StorageRole Role,
    bool IsActive,
    bool IsPickable,
    bool IsReplenishable,
    int ZonePickSequence,
    int PickSequence,
    decimal? MaxVolumeM3,
    decimal? MaxWeightKg,
    int? MaxDistinctItems) : Rules.IRuleCandidate
{
    /// <summary>The rack label. What a trace shows a human, and what they scan to confirm.</summary>
    string Rules.IRuleCandidate.Key => Code;
}

/// <summary>
/// MasterData's published read surface for locations.
/// <para>
/// Lives in SharedKernel rather than in MasterData so that a consumer takes a
/// reference on the contract without taking one on the implementation — the
/// module's DbContext, and therefore its tables, stay out of reach at compile
/// time rather than merely by convention.
/// </para>
/// </summary>
public interface ILocationDirectory
{
    Task<LocationSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<LocationSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// Batch lookup. Cross-module joins are impossible by design, so a listing
    /// endpoint resolves its foreign references in one round trip and stitches
    /// them in memory rather than issuing a query per row.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, LocationSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// The warehouse's virtual counterparty of the given kind — the "from" side
    /// of a receipt and the "to" side of a despatch.
    /// </summary>
    Task<LocationSnapshot?> FindCounterpartyAsync(
        Guid warehouseId,
        CounterpartyKind kind,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every physical location in a warehouse, as slotting candidates.
    /// <para>
    /// Deliberately unfiltered beyond dropping the virtual counterparties, which
    /// are not places. It is tempting to pre-narrow this to, say, the zones
    /// matching the item's temperature class — but a candidate excluded by a
    /// query never appears in the decision trace, and "the system never even
    /// looked at the chilled aisle" is exactly the answer a supervisor cannot
    /// get from Tier 2 software today. Narrowing here would move a rule out of
    /// the rule set and into a <c>WHERE</c> clause nobody can read, version or
    /// replay (ADR 0004). The rule set does the excluding, and says so.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StorageLocationSnapshot>> FindStorageCandidatesAsync(
        Guid warehouseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One physical location by its code, with its role and capacity.
    /// <para>
    /// The richer sibling of <see cref="FindByCodeAsync"/>, for the callers that
    /// need to know what a location is <i>for</i> — the inbound loop insisting a
    /// receipt lands on a dock rather than straight into racking. Returns null for
    /// a virtual counterparty, which is not a place.
    /// </para>
    /// </summary>
    Task<StorageLocationSnapshot?> FindStorageLocationByCodeAsync(
        string code,
        CancellationToken cancellationToken);
}
