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
}
