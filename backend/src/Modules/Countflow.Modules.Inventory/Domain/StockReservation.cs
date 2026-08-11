namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// Where a commitment is in its life.
/// <para>
/// Three states and no "partially picked". A reservation is created against one
/// holding for one quantity and is discharged in one movement, so a claim that
/// was half met would mean the pick itself was half posted — which the ledger's
/// transaction makes impossible. A short pick is a released reservation and a new
/// allocation, not a mutated one.
/// </para>
/// </summary>
public enum ReservationState
{
    /// <summary>Standing. Counts against available-to-promise and against nothing else.</summary>
    Open = 1,

    /// <summary>Discharged by the pick that fulfilled it, in the same transaction as the movement.</summary>
    Picked = 2,

    /// <summary>Given back — the order was cancelled, or an interrupted release let it go.</summary>
    Released = 3
}

/// <summary>
/// A claim on stock that has not moved yet.
/// <para>
/// <b>Why this is not a ledger entry.</b> The ledger records physical facts: this
/// quantity was in that place, and now it is in this one (ADR 0001). A
/// reservation asserts nothing physical — the pallet is exactly where it was, the
/// count on the shelf is unchanged, and a stocktake would find no trace of it.
/// Writing one as a movement would put a claim about the future into an
/// append-only record of the past, and every balance, replay and reconciliation
/// downstream would then be answering a question nobody asked.
/// </para>
/// <para>
/// <b>Why it lives here anyway.</b> Beside the ledger, in the same schema and the
/// same context, because a pick has to move the goods and discharge the promise
/// together or neither. Two schemas cannot share a transaction (ADR 0002), so a
/// reservation table in the outbound module would mean a crash between the two
/// writes leaving stock at the pack bench still committed to the order it has
/// already been picked for.
/// </para>
/// <para>
/// It names a <see cref="BalanceId"/> rather than repeating the item, lot,
/// location, plate and status it applies to. The holding is a coordinate with five
/// parts and copying them here would create a second place they could be wrong;
/// the balance row is the one place that already knows.
/// </para>
/// </summary>
public sealed class StockReservation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The holding being held back. Balance rows are never deleted, so this stays resolvable.</summary>
    public required Guid BalanceId { get; init; }

    /// <summary>In base units. Positive; a negative claim is a contradiction rather than a release.</summary>
    public required decimal Quantity { get; init; }

    public ReservationState State { get; set; } = ReservationState.Open;

    /// <summary>
    /// What has the claim — an order line id. Never resolved by this module: the
    /// reservation records that somebody has a claim, and whose it is belongs to
    /// whoever made it (ADR 0002).
    /// </summary>
    public required Guid OwnerId { get; init; }

    /// <summary>The same claim as a person reads it, e.g. <c>SO-2026-0007#2</c>.</summary>
    public required string OwnerReference { get; init; }

    /// <summary>
    /// The allocation decision that chose this holding. Nullable because the
    /// column is, and for the same reason the ledger's is: a future caller may
    /// legitimately hold stock without a rule set behind it. Everything Phase 4
    /// writes carries one.
    /// </summary>
    public Guid? DecisionTraceId { get; init; }

    /// <summary>Unique. A retried release must reserve the same stock once, not twice.</summary>
    public required string IdempotencyKey { get; init; }

    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The pick that discharged it. Set in the same transaction as that movement.</summary>
    public Guid? ConsumedByMovementId { get; set; }

    public string? ReleaseReason { get; set; }

    public StockBalance? Balance { get; init; }
}
