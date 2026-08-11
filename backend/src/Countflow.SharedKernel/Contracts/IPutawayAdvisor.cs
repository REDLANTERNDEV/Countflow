using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.SharedKernel.Contracts;

/// <summary>What the rules are being asked to slot.</summary>
public sealed record PutawayQuery
{
    public required Guid WarehouseId { get; init; }

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    /// <summary>In base units. Drives both the capacity constraints and the pallet-versus-handful preferences.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Where the goods are now — the dock. Travel is scored from here.</summary>
    public required Guid FromLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    public required PostingActor Actor { get; init; }
}

/// <summary>
/// The rules' answer, and the identifier of the trace that justifies it.
/// <para>
/// <see cref="LocationId"/> is nullable and <see cref="DecisionTraceId"/> is not.
/// That asymmetry is the point: a rule set is allowed to have no answer — a full
/// chilled aisle genuinely has nowhere to put another pallet — but it is never
/// allowed to have no reasoning. "Nothing was suitable, and here is why each
/// candidate failed" is a useful thing to hand an operator; "nothing was
/// suitable" on its own is what makes people stop trusting the system.
/// </para>
/// </summary>
public sealed record PutawayAdvice(
    Guid DecisionTraceId,
    Guid? LocationId,
    string? LocationCode,
    decimal Score,
    IReadOnlyList<ScoreLine> Reasons,
    int ConsideredCount,
    int AdmissibleCount);

/// <summary>
/// The Rules module's published advisory surface for slotting.
/// <para>
/// The trace is persisted before this returns, so the caller receives an
/// identifier it can stamp onto the movement it is about to post. That ordering
/// is deliberate: a movement pointing at a trace that was never written is worse
/// than no trace at all, because it looks like an explanation exists.
/// </para>
/// </summary>
public interface IPutawayAdvisor
{
    Task<Result<PutawayAdvice>> SuggestAsync(PutawayQuery query, CancellationToken cancellationToken);
}

/// <summary>One candidate as it was recorded, read back from storage.</summary>
public sealed record TracedCandidate(
    Guid CandidateId,
    string CandidateKey,
    int Rank,
    bool IsAdmissible,
    decimal Score,
    RuleNote? ExcludedBy,
    IReadOnlyList<ScoreLine> Scores);

/// <summary>
/// A stored decision, as shown on the supervisor console and behind the
/// handheld's "why here?".
/// </summary>
public sealed record DecisionTraceView(
    Guid Id,
    DecisionKind Kind,
    string RuleSetName,
    int RuleSetVersion,
    DateTimeOffset DecidedAt,
    int ConsideredCount,
    int AdmissibleCount,
    Guid? SelectedId,
    string? SelectedKey,
    IReadOnlyList<TracedCandidate> Candidates);

/// <summary>
/// Reads a persisted trace. Published separately from <see cref="IPutawayAdvisor"/>
/// because the modules that <i>make</i> decisions and the modules that
/// <i>explain</i> them are not the same set — Work never slots anything, but it
/// is the module an operator asks "why here?".
/// </summary>
public interface IDecisionTraceReader
{
    Task<DecisionTraceView?> FindAsync(Guid traceId, CancellationToken cancellationToken);
}
