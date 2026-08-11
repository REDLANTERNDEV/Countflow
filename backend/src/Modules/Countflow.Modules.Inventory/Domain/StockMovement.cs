namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// One immutable, double-entry ledger line: a quantity of one item and lot
/// leaving one holding and arriving at another.
/// <para>
/// <b>This table is append-only.</b> Never <c>UPDATE</c>, never <c>DELETE</c>
/// (ADR 0001). The rule is enforced three deep — every property here is
/// <c>init</c>-only, <c>InventoryDbContext.SaveChangesAsync</c> refuses to
/// persist a modified or deleted entry, and a database trigger rejects the
/// statement outright. Three guards because the cost of the invariant failing
/// silently is that the product's central claim stops being true, and nobody
/// finds out until someone counts a shelf.
/// </para>
/// <para>
/// Stock is never created or destroyed. Goods arrive <i>from</i> the Supplier
/// counterparty and leave <i>to</i> the Customer counterparty; a count loss moves
/// to Adjustment. Because every entry names both sides, the sum of every
/// quantity ever posted is exactly zero, and conservation is a property of the
/// schema rather than of a code path someone has to remember.
/// </para>
/// </summary>
public sealed class StockMovement
{
    /// <summary>
    /// Database-assigned, gap-free-per-transaction total order. Replay walks the
    /// ledger by this rather than by <see cref="OccurredAt"/>: two movements can
    /// share a timestamp, and "the balance as of 14 July" has to be a single
    /// definite answer.
    /// </summary>
    public long Sequence { get; init; }

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// When the movement happened, as recorded by the server. Not client-supplied:
    /// a handheld with a wrong clock must not be able to backdate the ledger.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>Null for items that are not lot-tracked. See <see cref="Lot"/>.</summary>
    public Guid? LotId { get; init; }

    /// <summary>
    /// Always positive, in the item's base unit of measure. Direction lives in
    /// the from/to pair, so a negative quantity here would mean the same thing
    /// twice and let one entry be written two ways.
    /// </summary>
    public required decimal Quantity { get; init; }

    // ---- The two sides ----
    //
    // A holding is (location, licence plate, status). All three can differ across
    // a single movement: picking a case off a pallet in quarantine into a
    // released tote changes every one of them at once, and that is one entry, not
    // three.

    public required Guid FromLocationId { get; init; }

    public required Guid ToLocationId { get; init; }

    public Guid? FromLicensePlateId { get; init; }

    public Guid? ToLicensePlateId { get; init; }

    public required InventoryStatus FromStatus { get; init; }

    public required InventoryStatus ToStatus { get; init; }

    // ---- Provenance ----

    public required MovementType Type { get; init; }

    public required MovementReason Reason { get; init; }

    /// <summary>
    /// Makes the write idempotent. Handhelds retry constantly — on a flaky
    /// wireless link a request that timed out may well have committed — and
    /// against an append-only ledger a retry that posts twice is a correctness
    /// bug, not a cosmetic one. Unique, so the second attempt is recognised as
    /// the same command and returns the original result.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The work assignment this movement discharged. Null for ad-hoc actions. Populated from Phase 3.</summary>
    public Guid? TaskId { get; init; }

    /// <summary>
    /// The rule evaluation that chose this movement (ADR 0004). Nullable only
    /// until Phase 3 puts the rules engine in front of putaway and allocation —
    /// after that, a directed movement without a trace is a bug.
    /// </summary>
    public Guid? DecisionTraceId { get; init; }

    /// <summary>
    /// Set on a compensating entry, pointing at the movement it undoes. The
    /// forward entry is left exactly as it was posted; this is what "corrections
    /// are compensating entries" means in practice.
    /// </summary>
    public Guid? ReversesMovementId { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>
    /// Copied rather than joined. Identity is another module's schema, and a
    /// ledger line from 2026 must still say who posted it after that account is
    /// renamed or deleted — an audit trail that can be altered by editing a
    /// different table is not an audit trail.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>Free text. Required by convention for manual corrections, where the reason code alone never suffices.</summary>
    public string? Notes { get; init; }
}
