using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// The disposition of a quantity of stock. Mirrors Inventory's
/// <c>InventoryStatus</c> — receiving suspect goods straight into quarantine is a
/// decision the inbound loop makes, so the vocabulary has to cross the boundary.
/// </summary>
public enum StockCondition
{
    Available = 1,
    Quarantine = 2,
    Damaged = 3,
    OnHold = 4,
    Expired = 5
}

/// <summary>Who a movement is posted on behalf of. Resolved from the access token by the calling module.</summary>
public sealed record PostingActor(Guid UserId, string UserName)
{
    private static readonly Error Unknown =
        Error.Unauthorized("auth.unknown_actor", "The access token carries no usable user identity.");

    /// <summary>
    /// Reads the actor out of a validated principal.
    /// <para>
    /// <c>sub</c> is looked up under <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
    /// because the JWT handler maps inbound claim types by default; the raw
    /// <c>sub</c> is checked too so this keeps working if that mapping is ever
    /// turned off.
    /// </para>
    /// </summary>
    public static Result<PostingActor> From(System.Security.Claims.ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (!Guid.TryParse(subject, out var userId))
        {
            return Result.Failure<PostingActor>(Unknown);
        }

        var name = principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
            ?? principal.Identity?.Name
            ?? subject;

        return new PostingActor(userId, name);
    }
}

/// <summary>What a caller learns from a posting it did not perform itself.</summary>
/// <param name="LotId">The batch the goods landed in — created on first sight by a receipt, so the caller cannot know it in advance.</param>
/// <param name="WasAlreadyPosted">True when the idempotency key had already been used for this same movement.</param>
public sealed record PostedMovement(
    Guid MovementId,
    long Sequence,
    DateTimeOffset OccurredAt,
    decimal Quantity,
    Guid? LotId,
    Guid? LicensePlateId,
    decimal FromQuantityAfter,
    decimal ToQuantityAfter,
    bool WasAlreadyPosted);

/// <summary>Books goods in from the Supplier counterparty.</summary>
public sealed record ReceiptInstruction
{
    public required Guid ItemId { get; init; }

    /// <summary>In the item's base unit. Packaging levels are resolved at the scan seam, never here.</summary>
    public required decimal Quantity { get; init; }

    public required Guid ToLocationId { get; init; }

    public string? LotNumber { get; init; }

    public DateOnly? ExpiryDate { get; init; }

    public DateOnly? ProductionDate { get; init; }

    /// <summary>The pallet's licence plate. Created on first sight.</summary>
    public string? Sscc { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    /// <summary>
    /// A customer return rather than a purchase-order receipt.
    /// <para>
    /// Changes the counterparty as well as the reason code, and that correction
    /// arrived with Phase 7. Until then a return credited the <b>Supplier</b>
    /// account while the despatch that sent the goods out had debited the
    /// <b>Customer</b> account — so the customer contra account only ever grew, and
    /// the one thing an append-only double-entry ledger is for (ADR 0001) was
    /// unavailable on the one account a distribution centre most wants to
    /// reconcile. A return now credits Customer, and
    /// <c>MovementShape</c> permits it.
    /// </para>
    /// </summary>
    public bool IsReturn { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public Guid? TaskId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Moves stock from the dock to the storage location the rules chose.
/// </summary>
/// <remarks>
/// Note what is <b>required</b> here and nullable on the ledger row itself:
/// <see cref="DecisionTraceId"/>. Putaway is directed work, and ADR 0004 says a
/// directed movement without a trace is a bug — so the published surface refuses
/// to express one. The column stays nullable because ad-hoc transfers and
/// corrections legitimately have no decision behind them; this contract is where
/// the stronger rule is enforced, at the only door directed work comes through.
/// </remarks>
public sealed record PutawayInstruction
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required decimal Quantity { get; init; }

    public required Guid FromLocationId { get; init; }

    public required Guid ToLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid DecisionTraceId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Moves everything on a licence plate in one go — the composite deferred from
/// Phase 2 because putaway is its first honest caller.
/// <para>
/// The lines are resolved from the plate's current balances rather than supplied,
/// because the operator scanning a pallet label knows the pallet, not its
/// contents. Every line lands in one transaction: a pallet that is half moved is
/// worse than one that is not moved at all, since the second is obvious and the
/// first is not.
/// </para>
/// </summary>
public sealed record PlatePutawayInstruction
{
    public required Guid LicensePlateId { get; init; }

    public required Guid FromLocationId { get; init; }

    public required Guid ToLocationId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid DecisionTraceId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Takes reserved stock out of its bin and drops it where the order is being
/// assembled.
/// <para>
/// The line is named by its <see cref="ReservationId"/> and not by an item,
/// a quantity and a location. That is the whole shape of the guarantee: a caller
/// cannot pick stock nobody promised, cannot pick more than was promised, and
/// cannot pick from a holding other than the one the rules chose — because none
/// of those are expressible. The reservation already says what is being taken
/// from where, and the pick discharges it in the same transaction as the movement
/// it posts.
/// </para>
/// </summary>
/// <param name="ToLocationId">
/// The pack bench or staging lane the picker drops at. Confirmed by scanning it,
/// so it is the location the operator was actually standing at rather than the one
/// the system expected.
/// </param>
public sealed record PickInstruction
{
    public required Guid ReservationId { get; init; }

    public required Guid ToLocationId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid DecisionTraceId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Tells the ledger what an operator actually found at a holding.
/// <para>
/// The first instruction in this contract that does not describe a movement. Every
/// other one says "these units went from here to there"; this one says "at this
/// place, at this moment, there were <see cref="CountedQuantity"/> of them" — and
/// what the ledger does about that is its own business. Often the answer is
/// nothing, because the shelf agreed with the book.
/// </para>
/// <para>
/// The holding is named by its parts rather than by a balance row id, and that is
/// load-bearing: <b>a count must be able to find stock where the system believes
/// there is none.</b> A balance row that has never existed has no id to quote, and
/// "there are twelve cases in a bay we think is empty" is one of the most valuable
/// things a count can discover.
/// </para>
/// </summary>
/// <param name="CountedQuantity">
/// In base units. Zero is legitimate and means the operator found an empty shelf;
/// it is not "no answer", which is why this is not nullable.
/// </param>
public sealed record CountInstruction
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid LocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    public required decimal CountedQuantity { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public required Guid TaskId { get; init; }

    /// <summary>
    /// The policy decision that raised this count. Required for the same reason
    /// <see cref="PutawayInstruction.DecisionTraceId"/> is: a count is directed
    /// work, somebody was sent to a bin rather than choosing it, and "why was I
    /// asked to count this?" has to be answerable.
    /// </summary>
    public required Guid DecisionTraceId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Writes stock off to the Scrap counterparty.
/// <para>
/// The other half of Phase 5, and the reason it is in this phase rather than any
/// other: a write-off is what a count variance turns into when the missing stock
/// is found and it is broken. Damaged goods are still on the shelf and still on
/// the book, so removing them is not a correction to a number — it is a real,
/// physical loss with a real counterparty, and the ledger says so.
/// </para>
/// </summary>
public sealed record ScrapInstruction
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid FromLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    public required decimal Quantity { get; init; }

    /// <summary>Why it is being destroyed. Damage, expiry or an ordered destruction.</summary>
    public required ScrapCause Cause { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    /// <summary>
    /// Not optional. Every other movement type is explained by the document behind
    /// it; a write-off is explained only by the person who ordered it.
    /// </summary>
    public required string Notes { get; init; }
}

/// <summary>Why stock is being written off. Maps to the ledger's own reason vocabulary.</summary>
public enum ScrapCause
{
    Damaged = 1,
    Expired = 2,
    Destroyed = 3
}

/// <summary>
/// Moves a quantity from one condition to another without moving it anywhere.
/// <para>
/// The operation the ledger has been able to post since Phase 1 —
/// <c>MovementType.StatusChange</c>, whose own documentation reads "quarantine,
/// release, write-off to damaged" — and which no published contract offered a door
/// onto until returns needed one. Quarantining a returned pallet is not a new
/// ledger capability; it is the first caller with a reason to ask for it.
/// </para>
/// <para>
/// Both conditions are named rather than only the destination. A caller that said
/// only "make this quarantined" would be asking the ledger to guess which of the
/// bay's holdings it meant, and a bay can hold the same item in two conditions at
/// once — which is the same reason an allocation key is location, batch and
/// condition rather than just location.
/// </para>
/// </summary>
public sealed record ConditionChangeInstruction
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid LocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public required decimal Quantity { get; init; }

    public required StockCondition FromCondition { get; init; }

    public required StockCondition ToCondition { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    /// <summary>
    /// The decision behind it, when there was one. Nullable rather than required,
    /// unlike <see cref="PutawayInstruction.DecisionTraceId"/>: a supervisor
    /// releasing a hold by hand is a legitimate status change with no rule set
    /// involved, and the disposition path supplies its trace anyway.
    /// </summary>
    public Guid? DecisionTraceId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Sends goods out to a third party who is expected to send them back.
/// <para>
/// A transfer to the InTransit counterparty, not a despatch — and the difference
/// is the point rather than a technicality. A despatch ends the warehouse's
/// interest in the stock and settles it against a customer; goods away for repair
/// are still the company's, still expected, and still owed a receipt. The ledger
/// already draws exactly that line: <c>MovementShape</c> has permitted InTransit on
/// either side of a <c>Transfer</c> since Phase 1, because stock leaving for
/// another site was the first thing that was neither here nor sold.
/// </para>
/// <para>
/// What comes back comes back as an ordinary receipt against the same counterparty,
/// which is why no second instruction is needed for the return leg.
/// </para>
/// </summary>
public sealed record RepairInstruction
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid FromLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public required decimal Quantity { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Damaged;

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    public Guid? DecisionTraceId { get; init; }

    /// <summary>
    /// Who is fixing it, and what is wrong with it. Required for the same reason
    /// <see cref="ScrapInstruction.Notes"/> is: nothing else in the system explains
    /// why a pallet left the building without being sold.
    /// </summary>
    public required string Notes { get; init; }
}

/// <summary>One item and batch leaving the building.</summary>
public sealed record DespatchLine(
    Guid ItemId,
    Guid? LotId,
    decimal Quantity,
    StockCondition Condition = StockCondition.Available);

/// <summary>
/// Sends the assembled order to the Customer counterparty.
/// <para>
/// Every line in one transaction, which is what <c>PostBatchAsync</c> was built
/// for: a shipment that is half despatched is worse than one that is not
/// despatched at all, because the trailer has left either way and only the second
/// state is obvious. Each line still carries its own key, derived from the root,
/// so a retry is recognised line by line.
/// </para>
/// </summary>
public sealed record DespatchInstruction
{
    /// <summary>Where the packed order is standing — a pack bench or a shipping lane.</summary>
    public required Guid FromLocationId { get; init; }

    public required IReadOnlyList<DespatchLine> Lines { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }

    /// <summary>The order this despatch settles. Recorded on every line, so the ledger can be read back by document.</summary>
    public string? Reference { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Inventory's published write surface — how another module moves stock without
/// being able to reach the ledger.
/// <para>
/// The methods name <i>operations</i>, not ledger primitives. There is no
/// <c>PostAsync(movementType, reason, …)</c> here on purpose: exposing the raw
/// vocabulary would let a consumer assemble a movement the shape rules were
/// never written for, and the whole value of the append-only ledger is that
/// every entry means what it says. A caller says "put this away"; Inventory
/// decides that is a <c>Putaway</c> for reason <c>PutawayComplete</c>, and keeps
/// deciding it in one place.
/// </para>
/// <para>
/// Every one of these still funnels into <c>StockLedger</c>'s single posting
/// path. This interface is a door onto that path, never a second one.
/// </para>
/// </summary>
public interface IStockPosting
{
    Task<Result<PostedMovement>> ReceiveAsync(
        ReceiptInstruction instruction,
        CancellationToken cancellationToken);

    Task<Result<PostedMovement>> PutawayAsync(
        PutawayInstruction instruction,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<PostedMovement>>> PutawayLicensePlateAsync(
        PlatePutawayInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves reserved stock to the pack bench and discharges the reservation that
    /// held it, together or not at all.
    /// </summary>
    Task<Result<PostedMovement>> PickAsync(
        PickInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>Moves reserved bulk stock into a pick face and consumes its claim atomically.</summary>
    Task<Result<PostedMovement>> ReplenishAsync(PickInstruction instruction, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<PostedMovement>>> DespatchAsync(
        DespatchInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what an operator found at a holding, and posts the difference if
    /// there is one.
    /// <para>
    /// The odd one out, and worth being explicit about why it belongs here anyway.
    /// Every other method on this interface posts a movement; this one records an
    /// observation and <i>may</i> post a movement. It is on the posting contract
    /// rather than a read surface because the two halves have to settle together —
    /// a count row without its adjustment claims the shelf was verified when the
    /// correction never landed, and an adjustment without its count row is a
    /// quantity change nobody can account for.
    /// </para>
    /// <para>
    /// Note what is <i>not</i> here, in keeping with the rest of the contract:
    /// there is no way to ask for a specific variance. The caller supplies what was
    /// found; Inventory reads what it believed and works out the difference, so a
    /// caller cannot post an adjustment for a number it made up.
    /// </para>
    /// </summary>
    Task<Result<CountOutcome>> CountAsync(
        CountInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>Destroys stock, against the Scrap counterparty.</summary>
    Task<Result<PostedMovement>> ScrapAsync(
        ScrapInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes what a quantity <i>is</i> without changing where it is.
    /// <para>
    /// The odd shape on a contract of movements, and it earns its place the same
    /// way <see cref="CountAsync"/> does: it is a real ledger entry with a real
    /// counterparty rule, and the alternative — a caller reaching for an adjustment
    /// out and an adjustment in — would post two movements that between them claim
    /// stock was destroyed and then created.
    /// </para>
    /// </summary>
    Task<Result<PostedMovement>> ChangeConditionAsync(
        ConditionChangeInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>Sends stock out for repair, against the InTransit counterparty.</summary>
    Task<Result<PostedMovement>> SendForRepairAsync(
        RepairInstruction instruction,
        CancellationToken cancellationToken);
}
