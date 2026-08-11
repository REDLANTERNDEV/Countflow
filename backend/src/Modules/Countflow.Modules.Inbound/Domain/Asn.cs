namespace Countflow.Modules.Inbound.Domain;

/// <summary>
/// How far along an advice note is.
/// <para>
/// Derived from its lines rather than set by hand — an ASN is "receiving" because
/// something has been received against it, not because somebody remembered to
/// press a button. State that has to be maintained separately from the facts it
/// summarises is state that eventually disagrees with them.
/// </para>
/// </summary>
public enum AsnStatus
{
    /// <summary>Announced, nothing received yet.</summary>
    Expected = 1,

    /// <summary>Part received. The common state for a multi-drop trailer.</summary>
    Receiving = 2,

    /// <summary>Every line received in full.</summary>
    Complete = 3,

    /// <summary>Closed with lines short. A deliberate decision, and one worth recording as such.</summary>
    Closed = 4,

    Cancelled = 5
}

/// <summary>
/// An advance shipping notice: what the supplier says is on the truck.
/// <para>
/// The point of an ASN is not paperwork, it is the comparison. Receiving against
/// an expectation turns "we got some boxes" into "we got four fewer than we were
/// told", at the dock, while the driver is still there — which is the only moment
/// a short delivery can be argued about cheaply.
/// </para>
/// <para>
/// Receiving without one is still allowed. A blind receipt is a real and common
/// event, and a system that refuses it just gets worked around.
/// </para>
/// </summary>
public sealed class Asn
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>The supplier's own document number. What everybody quotes on the phone.</summary>
    public required string Reference { get; set; }

    /// <summary>
    /// The supplier, as a MasterData business partner id. No foreign key across
    /// the schema boundary (ADR 0002); resolved for display.
    /// </summary>
    public Guid? SupplierId { get; init; }

    public string? SupplierName { get; set; }

    public AsnStatus Status { get; set; } = AsnStatus.Expected;

    public DateTimeOffset? ExpectedAt { get; set; }

    public string? CarrierReference { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ClosedAt { get; set; }

    public ICollection<AsnLine> Lines { get; init; } = [];
}

/// <summary>
/// One expected item on an advice note, and how much of it has actually turned
/// up.
/// </summary>
public sealed class AsnLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid AsnId { get; init; }

    /// <summary>Position on the supplier's document. Preserved so the two can be read side by side.</summary>
    public required int LineNumber { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>In the item's base unit — converted at the edge, like everything else.</summary>
    public required decimal ExpectedQuantity { get; set; }

    /// <summary>
    /// Running total, updated as receipts land. A projection of the ledger, and
    /// deliberately not the source of truth: the movements against this line's
    /// reference are, and this column is what makes the dock screen fast.
    /// </summary>
    public decimal ReceivedQuantity { get; set; }

    public string? ExpectedLotNumber { get; set; }

    public DateOnly? ExpectedExpiryDate { get; set; }

    public Asn? Asn { get; init; }

    public ICollection<AsnReceipt> Receipts { get; init; } = [];

    /// <summary>Positive means short, negative means over-delivered. Both are worth arguing about at the dock.</summary>
    public decimal OutstandingQuantity => ExpectedQuantity - ReceivedQuantity;
}

/// <summary>
/// One booking-in against a line.
/// <para>
/// The same argument as the stock ledger, one floor down (ADR 0001):
/// <see cref="AsnLine.ReceivedQuantity"/> is a projection and these rows are what
/// it projects. A running total incremented in place cannot be made idempotent —
/// a retried dock scan that adds the quantity twice is invisible until somebody
/// argues with the supplier about a delivery that was never short. An append with
/// a unique key can only land once, and the total is derived from what landed.
/// </para>
/// </summary>
public sealed class AsnReceipt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid AsnLineId { get; init; }

    /// <summary>The key the goods were posted to the ledger under. Unique, which is what makes this exactly-once.</summary>
    public required string IdempotencyKey { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>The ledger entry this booking created. No foreign key — Inventory is another schema.</summary>
    public required Guid MovementId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required string ReceivedByUserName { get; init; }

    public AsnLine? Line { get; init; }
}
