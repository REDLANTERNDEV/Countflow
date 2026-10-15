using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// One holding of one item, as an allocation decision sees it: what is
/// physically there, what is already promised to somebody else, and therefore
/// what is left.
/// <para>
/// This record is where the distinction Phase 2 deliberately did not draw
/// finally appears. <see cref="OnHandQuantity"/> is the ledger's projection — it
/// is what a stocktake would count. <see cref="AvailableQuantity"/> is what an
/// order may still be promised. They differ by the reservations standing against
/// the holding, and a warehouse that confuses the two sells the same pallet
/// twice.
/// </para>
/// <para>
/// Deliberately <b>not</b> pre-filtered to allocatable stock. A quarantined
/// pallet and a pallet still standing on the dock are both candidates offered to
/// the rule set and both excluded by a named rule, because "the system never even
/// looked at the stock in quarantine" is the answer a supervisor cannot get out
/// of a <c>WHERE</c> clause (ADR 0004, and the same argument as
/// <see cref="ILocationDirectory.FindStorageCandidatesAsync"/>).
/// </para>
/// </summary>
/// <param name="Id">
/// The balance row this holding is. A stable identifier for the trace, and the
/// reason a candidate can be pointed at afterwards at all — a holding is a
/// coordinate rather than an entity, so without this the trace would have nothing
/// to name.
/// </param>
/// <param name="ReservedQuantity">
/// Committed to orders that have not been picked yet. Can legitimately exceed
/// <paramref name="OnHandQuantity"/> for a moment — an ad-hoc transfer can move
/// stock out from under a reservation — which is why
/// <paramref name="AvailableQuantity"/> is clamped at zero rather than allowed
/// negative.
/// </param>
public sealed record StockHolding(
    Guid Id,
    Guid ItemId,
    Guid? LotId,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid LocationId,
    Guid? LicensePlateId,
    string? Sscc,
    StockCondition Condition,
    decimal OnHandQuantity,
    decimal ReservedQuantity) : Rules.IRuleCandidate
{
    public decimal AvailableQuantity => Math.Max(0m, OnHandQuantity - ReservedQuantity);

    /// <summary>
    /// What a person reads in the trace. Set by the caller that knows the
    /// location code, because Inventory does not — location codes are MasterData's
    /// (ADR 0002), and a trace showing GUIDs explains nothing.
    /// </summary>
    public string LocationCode { get; init; } = string.Empty;
    /// <summary>Earliest known receipt age of this holding's current cohort; null means unknown origin.</summary>
    public DateTimeOffset? FirstReceivedAt { get; init; }

    /// <summary>
    /// Location, batch and — when it is not the ordinary case — condition.
    /// <para>
    /// All three, because one bin routinely holds several holdings and a trace
    /// that showed the same key twice would be unreadable exactly where it matters
    /// most: a bay containing twenty-five good cases and twenty-five quarantined
    /// ones produces two candidates, one chosen and one refused, and "why not that
    /// one?" needs the two to be tellable apart. The condition is left off when it
    /// is Available so the common trace stays clean.
    /// </para>
    /// </summary>
    string Rules.IRuleCandidate.Key =>
        (LotNumber is null ? LocationCode : $"{LocationCode}/{LotNumber}")
        + (Condition == StockCondition.Available ? string.Empty : $" ({Condition})");
}

/// <summary>
/// Inventory's published read surface for "what can I promise?".
/// <para>
/// Separate from <see cref="IStockQuery"/>, which answers "where can this go?".
/// Both serve rules rather than people, and they are shaped for the two opposite
/// questions the warehouse asks.
/// </para>
/// </summary>
public interface IStockAvailability
{
    /// <summary>
    /// Every holding of one item in one warehouse that currently holds something,
    /// with its reservations already netted off.
    /// </summary>
    Task<IReadOnlyList<StockHolding>> HoldingsAsync(
        Guid warehouseId,
        Guid itemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// On-hand and reserved totals for a set of balance rows, for the enquiry
    /// screens. Absent ids simply hold nothing reserved.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> ReservedByBalanceAsync(
        IReadOnlyCollection<Guid> balanceIds,
        CancellationToken cancellationToken);
}

/// <summary>What an order line wants held back for it.</summary>
/// <param name="OwnerId">
/// The thing the reservation is for — an order line id. Opaque to Inventory,
/// which never resolves it: the reservation records that <i>somebody</i> has a
/// claim, and whose claim it is belongs to the module that made it.
/// </param>
/// <param name="OwnerReference">The same claim in a form a person reads, e.g. <c>SO-2026-0007#2</c>.</param>
public sealed record ReservationInstruction
{
    public required Guid BalanceId { get; init; }

    public required decimal Quantity { get; init; }

    public required Guid OwnerId { get; init; }

    public required string OwnerReference { get; init; }

    public Guid? DecisionTraceId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required PostingActor Actor { get; init; }
}

/// <summary>A held quantity, as the module that asked for it sees it.</summary>
/// <param name="WasAlreadyReserved">True when the idempotency key had already been used for this same claim.</param>
public sealed record ReservedStock(
    Guid Id,
    Guid BalanceId,
    Guid ItemId,
    Guid? LotId,
    Guid LocationId,
    Guid? LicensePlateId,
    decimal Quantity,
    Guid OwnerId,
    bool WasAlreadyReserved);

/// <summary>
/// Inventory's published write surface for commitments.
/// <para>
/// Reservations live in Inventory, beside the ledger and inside the same schema,
/// and that is a deliberate choice rather than a filing convenience: a pick has
/// to move the stock and discharge the promise in one transaction, and only code
/// that owns both tables can do that. What they are <b>not</b> is ledger entries.
/// A reserved quantity is a claim on stock, not a movement of it; nothing is
/// anywhere different because a reservation exists, so writing one to an
/// append-only ledger of physical facts would be recording a fact that is not
/// true yet (ADR 0001).
/// </para>
/// <para>
/// There is no <c>ConsumeAsync</c> here on purpose. A reservation is discharged by
/// the pick that fulfils it and in the same breath, so the only way to consume one
/// is <see cref="IStockPosting.PickAsync"/> — a caller cannot quietly drop a
/// commitment without moving the goods it was holding.
/// </para>
/// </summary>
public interface IStockReservations
{
    Task<Result<ReservedStock>> ReserveAsync(
        ReservationInstruction instruction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gives stock back. Idempotent: a reservation that is already released or
    /// already picked is left alone rather than refused, because the caller's
    /// intent — "this claim should not be standing" — is satisfied either way.
    /// </summary>
    Task<Result<int>> ReleaseAsync(
        IReadOnlyCollection<Guid> reservationIds,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Open claims made on behalf of one owner. How a release that was
    /// interrupted halfway finds the commitments its own bookkeeping never
    /// recorded, and lets them go before allocating again.
    /// </summary>
    Task<IReadOnlyList<ReservedStock>> FindOpenByOwnerAsync(
        Guid ownerId,
        CancellationToken cancellationToken);
}
