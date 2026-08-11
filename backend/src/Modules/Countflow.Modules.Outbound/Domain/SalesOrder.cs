namespace Countflow.Modules.Outbound.Domain;

/// <summary>
/// How far along an order is.
/// <para>
/// Five states, and deliberately no "picking" or "picked". Whether the picks are
/// done is a fact about the work queue, and asking it is cheap; storing a copy
/// here would mean maintaining a status that agrees with the queue right up until
/// the day it does not. What is stored is only what somebody explicitly did —
/// released it, packed it, sent it — because those are decisions rather than
/// summaries.
/// </para>
/// </summary>
public enum SalesOrderStatus
{
    /// <summary>Taken, and nothing committed to it yet.</summary>
    New = 1,

    /// <summary>Allocated by rule and the picks are in somebody's queue.</summary>
    Released = 2,

    /// <summary>Picked and made up at the bench. The goods are still in the building and still on the ledger.</summary>
    Packed = 3,

    /// <summary>Gone, and off the ledger — the Customer counterparty holds it now.</summary>
    Despatched = 4,

    Cancelled = 5
}

/// <summary>
/// What a customer has asked for.
/// <para>
/// The mirror of an advice note, and the same argument applies: the value is not
/// the paperwork, it is the comparison. An order is what turns "some stock left
/// the building" into "this stock left against this promise", which is the only
/// way a short shipment can be noticed before the customer notices it.
/// </para>
/// </summary>
public sealed class SalesOrder
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>The order number everybody quotes on the phone.</summary>
    public required string Reference { get; set; }

    /// <summary>
    /// The customer, as a MasterData business partner id. No foreign key across
    /// the schema boundary (ADR 0002); resolved for display.
    /// </summary>
    public Guid? CustomerId { get; init; }

    public string? CustomerName { get; set; }

    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.New;

    /// <summary>
    /// Where the picks are dropped and the order is made up — a pack bench or a
    /// shipping lane.
    /// <para>
    /// Chosen by a person today. Which bench an order should be built on is a
    /// genuine decision with real consequences for travel and congestion, and it
    /// is exactly the shape of thing <c>DecisionKind.Waving</c> exists for — so it
    /// is a field for now rather than a guess the system makes and cannot explain.
    /// </para>
    /// </summary>
    public Guid? StagingLocationId { get; set; }

    /// <summary>When the goods are due to leave. Drives the shelf-life rule, which measures from the shipping day.</summary>
    public DateOnly? ShipBy { get; set; }

    public Guid? WaveId { get; set; }

    public int Priority { get; set; } = 50;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ReleasedAt { get; set; }

    public DateTimeOffset? PackedAt { get; set; }

    public string? PackedByUserName { get; set; }

    public DateTimeOffset? DespatchedAt { get; set; }

    public string? DespatchedByUserName { get; set; }

    /// <summary>
    /// The key the despatch posted under. Stored for the same reason a work task
    /// stores its completion key: a retry with the same key replays, and a
    /// different key against an already-despatched order is the second despatch it
    /// is rather than a duplicate trailer.
    /// </summary>
    public string? DespatchIdempotencyKey { get; set; }

    public string? CancelledReason { get; set; }

    public ICollection<SalesOrderLine> Lines { get; init; } = [];
}

/// <summary>One item on an order, and how much of it has been committed and picked.</summary>
public sealed class SalesOrderLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid SalesOrderId { get; init; }

    /// <summary>Position on the customer's document. Preserved so the two can be read side by side.</summary>
    public required int LineNumber { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>In the item's base unit — converted at the edge, like everything else.</summary>
    public required decimal OrderedQuantity { get; set; }

    /// <summary>
    /// A batch the customer named. Rare; when present it is absolute, and the
    /// allocation rules treat it as a hard constraint rather than a preference.
    /// </summary>
    public Guid? RequiredLotId { get; init; }

    /// <summary>
    /// Committed to this line, and a projection of <see cref="Allocations"/>
    /// rather than a figure anybody sets.
    /// <para>
    /// The same argument as <c>AsnLine.ReceivedQuantity</c>, and the third time
    /// the system makes it (ADR 0001, one floor down again): a running total
    /// incremented in place cannot be made idempotent, and a retried release that
    /// added its quantity twice would commit stock to an order nobody can pick.
    /// </para>
    /// </summary>
    public decimal AllocatedQuantity { get; set; }

    public SalesOrder? Order { get; init; }

    public ICollection<OrderLineAllocation> Allocations { get; init; } = [];

    /// <summary>Positive means the line is short of stock. Zero is the normal, happy case.</summary>
    public decimal OutstandingQuantity => OrderedQuantity - AllocatedQuantity;
}

/// <summary>
/// One holding committed to one order line, and the job that fetches it.
/// <para>
/// Append-only, like <c>AsnReceipt</c> and <c>WorkTaskSource</c> before it. A row
/// is written once when the rules choose a holding and never rewritten;
/// cancelling an order releases the reservation and cancels the task, and this row
/// remains as the record that the commitment was once made — which is what makes
/// "why did this order take four days" answerable afterwards.
/// </para>
/// <para>
/// It carries three identifiers belonging to three other modules and resolves none
/// of them: the reservation is Inventory's, the task is Work's, the trace is
/// Rules'. That is the shape of an orchestrator (ADR 0002) — it remembers what it
/// asked for and asks the owner what became of it.
/// </para>
/// </summary>
public sealed class OrderLineAllocation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid SalesOrderLineId { get; init; }

    /// <summary>The release that produced this row. What makes a retried release recognise its own earlier attempt.</summary>
    public required string ReleaseIdempotencyKey { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>Inventory's claim on the stock. Discharged by the pick, never by this module.</summary>
    public required Guid StockReservationId { get; init; }

    /// <summary>Work's job. Asked about rather than watched — see <c>IWorkQueue.FindManyAsync</c>.</summary>
    public Guid? WorkTaskId { get; set; }

    /// <summary>Rules' evaluation. The answer to "why this batch?", months later.</summary>
    public required Guid DecisionTraceId { get; init; }

    // ---- What was chosen, copied for display ----
    //
    // Denormalised deliberately, and only for reading. The reservation is the
    // truth about what is held; these are what the screen prints so a page of
    // allocations does not become a page of cross-module lookups. They are never
    // read back to make a decision.

    public required Guid LocationId { get; init; }

    public required string LocationCode { get; init; }

    public Guid? LotId { get; init; }

    public string? LotNumber { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public required DateTimeOffset AllocatedAt { get; init; }

    public SalesOrderLine? Line { get; init; }
}
