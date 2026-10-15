using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>What the rules are being asked to find stock for.</summary>
/// <param name="RequiredLotId">
/// A batch the customer named. Rare, and when it happens it is absolute — a
/// pharmacy recall replacement or a retailer's own quality hold — so it is a hard
/// constraint rather than a preference.
/// </param>
/// <param name="ShipBy">
/// When the goods are due to leave. The minimum-remaining-shelf-life rule measures
/// from this rather than from today, because what the customer's contract cares
/// about is the life left when the pallet reaches them, and an order picked today
/// for a Friday trailer has three fewer days than it looks.
/// </param>
public sealed record AllocationQuery
{
    public required Guid WarehouseId { get; init; }

    public required Guid ItemId { get; init; }

    public Guid? RequiredLotId { get; init; }

    /// <summary>Operation boundary, e.g. replenishment may draw only from reserve storage.</summary>
    public StorageRole? RequiredSourceRole { get; init; }

    /// <summary>In base units, and what is still outstanding rather than what was ordered.</summary>
    public required decimal Quantity { get; init; }

    public DateOnly? ShipBy { get; init; }

    public required PostingActor Actor { get; init; }
}

/// <summary>
/// One holding the rules chose, and how much of it to take.
/// </summary>
/// <param name="Quantity">
/// What to draw from this holding, which is the demand or the holding's available
/// quantity, whichever is smaller. The rules rank; the fill walks down the ranking
/// taking what it can, which is why one order line can produce several of these.
/// </param>
public sealed record AllocationLine(
    Guid BalanceId,
    Guid ItemId,
    Guid? LotId,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid LocationId,
    string LocationCode,
    Guid? LicensePlateId,
    decimal Quantity,
    decimal Score);

/// <summary>
/// The rules' answer for one order line, and the identifier of the trace that
/// justifies it.
/// <para>
/// The asymmetry from <see cref="PutawayAdvice"/> is repeated here and for the
/// same reason: <see cref="Lines"/> may be empty and
/// <see cref="DecisionTraceId"/> may not. An item with nothing allocatable in the
/// building is an ordinary answer, and "here is why each holding was refused" is
/// the only useful thing to tell the person who has to phone the customer.
/// </para>
/// <para>
/// <see cref="ShortfallQuantity"/> being positive is not a failure either. A line
/// half-allocated is half-picked today and the rest chased tomorrow, which is what
/// warehouses actually do; refusing the whole line because the last twelve units
/// are missing would hold up the ninety that are there.
/// </para>
/// </summary>
public sealed record AllocationPlan(
    Guid DecisionTraceId,
    IReadOnlyList<AllocationLine> Lines,
    decimal AllocatedQuantity,
    decimal ShortfallQuantity,
    int ConsideredCount,
    int AdmissibleCount);

/// <summary>
/// The Rules module's published advisory surface for choosing stock.
/// <para>
/// The second decision kind, and the test of whether the engine generalised.
/// Nothing about it is putaway-shaped: the candidates are holdings rather than
/// locations, one evaluation yields several lines rather than one winner, and the
/// question is "which stock" rather than "which bin" — and it still runs through
/// the same <c>RuleEngine</c>, the same <c>Decision&lt;T&gt;</c> and the same
/// journal. If it had needed a second evaluator, ADR 0004's claim that one
/// mechanism serves every directed decision would have been wrong.
/// </para>
/// <para>
/// As with putaway, the trace is committed before this returns, so the caller
/// holds an identifier it can stamp onto the reservations and the work it is about
/// to create.
/// </para>
/// </summary>
public interface IAllocationAdvisor
{
    Task<Result<AllocationPlan>> AllocateAsync(AllocationQuery query, CancellationToken cancellationToken);
}
