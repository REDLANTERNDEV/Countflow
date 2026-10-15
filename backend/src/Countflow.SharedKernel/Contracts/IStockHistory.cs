namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// One holding as it stood at a point in the ledger.
/// <para>
/// Deliberately thinner than either <see cref="StockHolding"/> or
/// <see cref="CountCandidate"/>: it carries what the ledger alone can say, and
/// nothing that has to be looked up somewhere else. Lot numbers, expiry dates and
/// SSCCs are the same today as they were then, so a caller that needs them reads
/// them from <see cref="IStockQuery"/> at present-day cost rather than having them
/// dragged through the fold.
/// </para>
/// </summary>
/// <param name="BalanceId">
/// The projection row this holding is. Resolvable for anything that ever existed,
/// because balance rows are created on first posting and never deleted — which is
/// what lets a reconstructed candidate be named the same way a live one is.
/// </param>
/// <param name="LastMovementSequence">
/// The last movement folded into this holding <b>at or before the checkpoint</b>,
/// not the last one ever. A replay that reported today's figure here would tell a
/// counting policy that a bay had been worked since a count that had not happened
/// yet.
/// </param>
public sealed record HistoricalHolding(
    Guid BalanceId,
    Guid ItemId,
    Guid? LotId,
    Guid LocationId,
    Guid? LicensePlateId,
    StockCondition Condition,
    decimal Quantity,
    long LastMovementSequence,
    DateTimeOffset LastMovedAt)
{
    public DateTimeOffset? FirstReceivedAt { get; init; }
}

/// <summary>When a holding was last verified, as of a checkpoint.</summary>
public sealed record HoldingCountHistory(DateTimeOffset CountedAt, long AtMovementSequence);

/// <summary>
/// The warehouse as it stood at one instant, rebuilt from history.
/// </summary>
/// <param name="Sequence">
/// The last ledger sequence at or before <see cref="AsOf"/>, or null if the ledger
/// did not exist yet. A sequence rather than the timestamp, because several
/// movements land in the same millisecond during a wave and "the balance at
/// 14:32:07.421" has to have exactly one answer.
/// </param>
/// <param name="ReservedByBalance">
/// Claims that were standing at this instant, by holding.
/// <para>
/// ADR 0001 said reservations were the one input a replay could not reconstruct,
/// because they are current state rather than an append-only history. That turns
/// out to be half true and worth correcting: a reservation carries
/// <c>CreatedAt</c> and a set-once <c>ClosedAt</c>, rows are never deleted, and
/// quantity is immutable — so the claims open at an instant are recoverable
/// exactly. What is <i>not</i> recoverable is a claim whose row has been purged by
/// some future retention policy, which is why replay reports fidelity per decision
/// rather than asserting it.
/// </para>
/// </param>
public sealed record WarehouseAsOf(
    DateTimeOffset AsOf,
    long? Sequence,
    IReadOnlyList<HistoricalHolding> Holdings,
    IReadOnlyDictionary<Guid, decimal> ReservedByBalance,
    IReadOnlyDictionary<Guid, HoldingCountHistory> CountedByBalance);

/// <summary>
/// Inventory's published read surface for "what did this look like then?".
/// <para>
/// This is the contract ADR 0001 has been promising since Phase 2. The ledger is a
/// complete, immutable history, so the state any past decision saw is
/// reconstructible — and that is the whole reason a rule change can be tested
/// against reality instead of against a guess (ADR 0004). Separate from
/// <see cref="IStockQuery"/>, which answers "where can this go <i>now</i>", for the
/// same reason that one is separate from <see cref="IStockAvailability"/>: three
/// questions, three shapes, and a single fat contract would hand every consumer all
/// three.
/// </para>
/// </summary>
public interface IStockHistory
{
    /// <summary>
    /// The last sequence posted at or before an instant, or null if the ledger did
    /// not exist yet.
    /// </summary>
    Task<long?> ResolveSequenceAsync(DateTimeOffset asOf, CancellationToken cancellationToken);

    /// <summary>
    /// Walks history once, yielding the warehouse as it stood at each checkpoint.
    /// <para>
    /// <b>Why a walk and not a query per instant.</b> Replaying a rule set over a
    /// month of decisions means answering "what was here then?" once per decision,
    /// and summing the whole ledger each time is quadratic in history — it works on
    /// a seeded database and dies on the first real quarter. Folding the movement
    /// stream forward once and pausing at each checkpoint is linear in both, which
    /// is the difference between a replay a supervisor waits for and one they give
    /// up on.
    /// </para>
    /// <para>
    /// Each snapshot is materialised and independent, so a consumer may hold one
    /// while it works. Checkpoints must be ascending; the fold only goes forwards.
    /// </para>
    /// </summary>
    /// <param name="locationIds">
    /// The physical locations in scope. Location-to-warehouse is MasterData's fact
    /// (ADR 0002), so the caller — which has already fetched the candidates it is
    /// deciding over — supplies the narrowing rather than this module inventing a
    /// join it is not allowed to make.
    /// </param>
    IAsyncEnumerable<WarehouseAsOf> WalkAsync(
        IReadOnlyCollection<Guid> locationIds,
        IReadOnlyList<DateTimeOffset> checkpoints,
        CancellationToken cancellationToken);
}
