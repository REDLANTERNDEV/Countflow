namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// One holding as a counting decision sees it: what the system believes is there,
/// when anybody last checked, and whether it has moved since.
/// <para>
/// The counterpart of <see cref="StockHolding"/>, and the difference between the
/// two is the whole of Phase 5. An allocation candidate is described by what can
/// be promised from it; a count candidate is described by <i>how much the record
/// is worth believing</i> — which is why the interesting fields here are all
/// timestamps and sequence numbers rather than quantities.
/// </para>
/// <para>
/// Offered unfiltered, exactly as putaway's locations and allocation's holdings
/// are (ADR 0004). A holding excluded by a <c>WHERE</c> clause never appears in
/// the trace, and "why was that bay not counted" is a question a supervisor has to
/// be able to get an answer to — the rule set does the excluding, and says so.
/// </para>
/// </summary>
/// <param name="Id">
/// The balance row. The same identifier an allocation candidate uses, so a
/// holding is named the same way whichever decision is looking at it.
/// </param>
/// <param name="LastCountedAt">
/// When this holding was last counted, variance or not. Null means never — which
/// is not a missing value but the strongest possible signal, and the reason this
/// is nullable rather than defaulted to some epoch.
/// </param>
/// <param name="LastMovementSequence">
/// The ledger sequence of the last movement that touched this holding, from the
/// projection.
/// </param>
/// <param name="SequenceAtLastCount">
/// What <paramref name="LastMovementSequence"/> read when the holding was last
/// counted. The pair is what makes <see cref="HasMovedSinceCount"/> answerable
/// without a second query.
/// </param>
/// <param name="LastMovedAt">
/// When the holding was last written to. Counting a bay a putaway landed in ten
/// minutes ago measures the truck rather than the record, so this is what a
/// quiet-period constraint reads.
/// </param>
public sealed record CountCandidate(
    Guid Id,
    Guid ItemId,
    Guid? LotId,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid LocationId,
    Guid? LicensePlateId,
    StockCondition Condition,
    decimal OnHandQuantity,
    DateTimeOffset? LastCountedAt,
    long LastMovementSequence,
    long? SequenceAtLastCount,
    DateTimeOffset LastMovedAt) : Rules.IRuleCandidate
{
    /// <summary>
    /// What a person reads in the trace. Set by the caller that knows the location
    /// code, for the reason <see cref="StockHolding.LocationCode"/> is: location
    /// codes belong to MasterData (ADR 0002).
    /// </summary>
    public string LocationCode { get; init; } = string.Empty;

    /// <summary>
    /// Whether anything has happened to this holding since it was last verified.
    /// <para>
    /// A boolean rather than a count of movements, and deliberately so. The
    /// ledger's sequence is global, so the gap between two sequence numbers
    /// measures how busy the <i>warehouse</i> has been rather than how busy this
    /// bin has been, and reporting that difference as "movements since count"
    /// would be a number that looks precise and means nothing. Counting the
    /// movements properly is a correlated scan of the ledger per candidate, which
    /// is not worth it for a preference that only has to separate "untouched since
    /// somebody checked" from "worked since somebody checked".
    /// </para>
    /// <para>
    /// Never counted reads as moved. A holding nobody has ever verified is not a
    /// holding at rest.
    /// </para>
    /// </summary>
    public bool HasMovedSinceCount =>
        SequenceAtLastCount is not { } counted || LastMovementSequence > counted;

    /// <summary>
    /// Location, batch and — when it is not the ordinary case — condition. The
    /// same key an allocation candidate uses, because one bin routinely holds
    /// several holdings and a trace showing the same key twice is unreadable
    /// exactly where it matters most.
    /// </summary>
    string Rules.IRuleCandidate.Key =>
        (LotNumber is null ? LocationCode : $"{LocationCode}/{LotNumber}")
        + (Condition == StockCondition.Available ? string.Empty : $" ({Condition})");
}

/// <summary>
/// What one completed count found.
/// </summary>
/// <param name="Variance">
/// <c>Counted − Expected</c>. Positive is the common case and not an error: it is
/// how a warehouse discovers a mis-pick from three weeks ago, and a system that
/// refused it would simply be told the number it already believed.
/// </param>
/// <param name="Adjustment">
/// The ledger entry the variance posted, or null when there was none. <b>Null is
/// the good outcome</b> — a count that agrees with the book moves nothing, and
/// this is the one operation in the system whose success is routinely silent in
/// the ledger. That is exactly why the count is recorded separately from it.
/// </param>
public sealed record CountOutcome(
    Guid CountId,
    decimal ExpectedQuantity,
    decimal CountedQuantity,
    decimal Variance,
    PostedMovement? Adjustment,
    bool WasAlreadyCounted);

/// <summary>
/// Inventory's published read surface for "which of these numbers do we least
/// believe?", and for what the counts have found.
/// <para>
/// Separate from <see cref="IStockAvailability"/> for the same reason that is
/// separate from <see cref="IStockQuery"/>: they answer three different questions
/// and a single fat contract would hand every consumer all three.
/// </para>
/// </summary>
public interface IStockCounts
{
    /// <summary>
    /// Every holding in a warehouse that a count could be raised against, with its
    /// counting history attached.
    /// </summary>
    Task<IReadOnlyList<CountCandidate>> CandidatesAsync(
        Guid warehouseId,
        CancellationToken cancellationToken);
}
