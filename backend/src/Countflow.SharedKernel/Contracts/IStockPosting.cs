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

    /// <summary>A customer return rather than a purchase-order receipt. Same movement, different reason code.</summary>
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
}
