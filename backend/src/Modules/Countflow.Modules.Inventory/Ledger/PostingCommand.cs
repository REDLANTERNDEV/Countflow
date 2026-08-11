using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Who posted a movement, resolved from the access token.
/// <para>
/// Captured as values rather than a user id to join on later: see the note on
/// <see cref="StockMovement.UserName"/> about why an audit trail must not be
/// editable from another schema.
/// </para>
/// </summary>
public sealed record LedgerActor(Guid UserId, string UserName)
{
    /// <summary>
    /// Reads the actor out of a validated principal.
    /// <para>
    /// <c>sub</c> is looked up under <see cref="ClaimTypes.NameIdentifier"/>
    /// because the JWT handler maps inbound claim types by default; the raw
    /// <c>sub</c> is checked too so this keeps working if that mapping is ever
    /// turned off.
    /// </para>
    /// </summary>
    public static Result<LedgerActor> From(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (!Guid.TryParse(subject, out var userId))
        {
            return Result.Failure<LedgerActor>(InventoryErrors.UnknownActor);
        }

        var name = principal.FindFirst(ClaimTypes.Name)?.Value
            ?? principal.Identity?.Name
            ?? subject;

        return new LedgerActor(userId, name);
    }
}

/// <summary>
/// A request to post one ledger entry. Everything the invariants need, and
/// nothing the caller should be able to choose — notably not the timestamp and
/// not the sequence number.
/// </summary>
public sealed record PostingCommand
{
    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    /// <summary>In the item's base unit of measure. Positive; direction is <see cref="From"/> → <see cref="To"/>.</summary>
    public required decimal Quantity { get; init; }

    public required Holding From { get; init; }

    public required Holding To { get; init; }

    public required MovementType Type { get; init; }

    public required MovementReason Reason { get; init; }

    /// <summary>See <see cref="StockMovement.IdempotencyKey"/>. Not optional, ever.</summary>
    public required string IdempotencyKey { get; init; }

    public required LedgerActor Actor { get; init; }

    public Guid? TaskId { get; init; }

    public Guid? DecisionTraceId { get; init; }

    public Guid? ReversesMovementId { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    /// True when the command describes the same movement as an entry already in
    /// the ledger. Compared field by field rather than by a stored hash, so a
    /// change to the command shape cannot silently start matching everything.
    /// </summary>
    public bool Matches(StockMovement movement) =>
        movement.ItemId == ItemId
        && movement.LotId == LotId
        && movement.Quantity == Quantity
        && movement.FromLocationId == From.LocationId
        && movement.FromLicensePlateId == From.LicensePlateId
        && movement.FromStatus == From.Status
        && movement.ToLocationId == To.LocationId
        && movement.ToLicensePlateId == To.LicensePlateId
        && movement.ToStatus == To.Status
        && movement.Type == Type
        && movement.Reason == Reason;
}

/// <summary>
/// What the caller learns from a successful posting: the ledger line it created,
/// and where both sides now stand.
/// </summary>
/// <param name="WasAlreadyPosted">
/// True when this command had already been applied and nothing was written this
/// time. The caller treats it exactly like a success — that is the entire point
/// of the idempotency key — but the flag is reported so a developer chasing a
/// duplicate-scan complaint can see what happened.
/// </param>
public sealed record MovementReceipt(
    Guid MovementId,
    long Sequence,
    DateTimeOffset OccurredAt,
    decimal Quantity,
    decimal FromQuantityAfter,
    decimal ToQuantityAfter,
    bool WasAlreadyPosted);
