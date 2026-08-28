using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Outbound.Domain;

/// <summary>
/// How far along a return is.
/// <para>
/// Two states, and derived rather than set — the same discipline
/// <see cref="Asn"/>'s status follows. A return is finished when every line has
/// been dispositioned in full, which is a fact about the lines; storing a third
/// state somebody presses a button for would be a status that agrees with the
/// lines right up until the day it does not.
/// </para>
/// </summary>
public enum ReturnStatus
{
    /// <summary>Booked in and standing in the returns bay. An inspection is in somebody's queue.</summary>
    Received = 1,

    /// <summary>Every line has been decided and posted. Nothing is left standing in the bay against it.</summary>
    Dispositioned = 2
}

/// <summary>
/// Goods a customer sent back.
/// <para>
/// <b>Owned by Outbound, and that is the answer to "which module owns a return?"
/// rather than an accident of where it was written.</b> A return is inbound goods
/// against an <i>outbound</i> document: it is judged against what was despatched,
/// it settles against the customer account the despatch debited, and
/// <see cref="SalesOrderLine.ReturnedQuantity"/> is a projection over these rows.
/// Putting the aggregate in Inbound would put that projection across a schema
/// boundary, and ADR 0002 says a module never reads another's tables — so Outbound
/// would have to learn what came back by asking Inbound, per line, on every read of
/// an order.
/// </para>
/// <para>
/// The physical half still goes through the published door, exactly as a despatch
/// does: <c>IStockPosting.ReceiveAsync</c> books the goods, <c>IWorkQueue</c>
/// raises the inspection, <c>IDispositionAdvisor</c> decides their fate. This
/// module orchestrates and writes no ledger entry, which is the same claim
/// <see cref="OutboundModule"/> has been able to make since Phase 4.
/// </para>
/// <para>
/// A return with no order behind it is legitimate and common — somebody sends back
/// a pallet with a scribbled note — and is the exact mirror of the blind receipt
/// the dock has always accepted. <see cref="SalesOrderId"/> is therefore nullable,
/// and the rules score a blind return's freshness at the midpoint rather than
/// inventing a despatch date.
/// </para>
/// </summary>
public sealed class Return
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>The RMA number, or whatever the desk writes on the paperwork.</summary>
    public required string Reference { get; set; }

    /// <summary>The order it came back from, when there is one.</summary>
    public Guid? SalesOrderId { get; init; }

    /// <summary>Copied for display, like the allocation's location code. Never read back to decide anything.</summary>
    public string? SalesOrderReference { get; init; }

    public string? CustomerName { get; set; }

    /// <summary>
    /// The returns bay the goods were booked into. A hold-role location, insisted
    /// on at booking: stock whose condition nobody has established yet must not
    /// land anywhere it could be allocated from.
    /// </summary>
    public required Guid ReceivedAtLocationId { get; init; }

    public ReturnStatus Status { get; set; } = ReturnStatus.Received;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? CreatedByUserName { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public string? Notes { get; set; }

    public ICollection<ReturnLine> Lines { get; init; } = [];
}

/// <summary>
/// One item and batch that came back, and what has been decided about it.
/// </summary>
public sealed class ReturnLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ReturnId { get; init; }

    public required int LineNumber { get; init; }

    /// <summary>
    /// The order line this came back from. Null on a blind return, and null when
    /// the customer sent back something that was never on the order — which happens,
    /// and which is worth recording as what it is rather than refusing at the desk.
    /// </summary>
    public Guid? SalesOrderLineId { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>In the item's base unit — converted at the scan seam, like everything else.</summary>
    public required decimal Quantity { get; init; }

    public Guid? LotId { get; init; }

    public string? LotNumber { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public Guid? LicensePlateId { get; init; }

    /// <summary>The ledger entry that booked the goods in. What makes this line a physical fact rather than a claim.</summary>
    public required Guid ReceiptMovementId { get; init; }

    /// <summary>
    /// The job that asks somebody to open the carton. Nullable for the same reason
    /// <c>OrderLineAllocation.WorkTaskId</c> is: the receipt and the task are two
    /// writes in two schemas, and a crash between them must leave a repairable
    /// state rather than an impossible one.
    /// </summary>
    public Guid? InspectionTaskId { get; set; }

    /// <summary>
    /// What has actually been decided and posted, and a projection of
    /// <see cref="Dispositions"/> rather than a figure anybody sets.
    /// <para>
    /// The fourth time this system makes the argument, and it does not get weaker
    /// with repetition (ADR 0001): a running total incremented in place cannot be
    /// made idempotent, and a retried disposition that added its quantity twice
    /// would close a line that still has goods standing in the bay.
    /// </para>
    /// </summary>
    public decimal DispositionedQuantity { get; set; }

    public Return? Return { get; init; }

    public ICollection<ReturnLineDisposition> Dispositions { get; init; } = [];

    /// <summary>What is still standing in the returns bay waiting for somebody to decide.</summary>
    public decimal OutstandingQuantity => Quantity - DispositionedQuantity;
}

/// <summary>
/// One decision about one returned quantity, and the movement that carried it out.
/// <para>
/// Append-only, like <c>AsnReceipt</c> and <c>OrderLineAllocation</c> before it. A
/// row is written once and never rewritten, which is what lets a line be
/// dispositioned in parts — four of the six went back on the shelf and two were
/// scrapped is a completely ordinary outcome, and it is two rows.
/// </para>
/// <para>
/// <see cref="DecisionTraceId"/> is <b>required</b>, and this is the door where
/// that rule is enforced. The rules run on every disposition, including the ones a
/// person overrides: an override records what the rules said <i>and</i> that
/// somebody disagreed, because "the supervisor overruled it" is only a useful
/// thing to know next to what was overruled.
/// </para>
/// </summary>
public sealed class ReturnLineDisposition
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ReturnLineId { get; init; }

    /// <summary>What was actually done — which is the override when there was one, not the advice.</summary>
    public required Disposition Disposition { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>Rules' evaluation. The answer to "why was this destroyed?", months later.</summary>
    public required Guid DecisionTraceId { get; init; }

    /// <summary>
    /// True when a person chose something other than what the rules ranked first.
    /// <para>
    /// Recorded rather than inferred by comparing against the trace, and the
    /// difference matters: the trace's winner is what the rule set said <i>then</i>,
    /// and a rule set is promotable data that will have moved on by the time anybody
    /// asks. A boolean written at the moment of disagreement stays true.
    /// </para>
    /// </summary>
    public bool WasOverridden { get; init; }

    /// <summary>The ledger entry that carried it out. Every disposition posts exactly one.</summary>
    public required Guid MovementId { get; init; }

    /// <summary>The putaway raised by a restock. Null for the other three, which move the goods themselves.</summary>
    public Guid? WorkTaskId { get; set; }

    /// <summary>What makes a retried disposition recognise its own earlier attempt.</summary>
    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    public string? DecidedByUserName { get; init; }

    /// <summary>
    /// The inspector's words. Required by the endpoint for a scrap, and carried
    /// onto the movement — <c>ScrapInstruction.Notes</c> is not optional, because a
    /// write-off is explained only by the person who ordered it.
    /// </summary>
    public string? Notes { get; init; }

    public ReturnLine? Line { get; init; }
}
