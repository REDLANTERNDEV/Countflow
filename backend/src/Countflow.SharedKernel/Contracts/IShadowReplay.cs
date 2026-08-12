using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// How much a replayed comparison is worth.
/// <para>
/// <b>Measured, not asserted.</b> Every replay runs the rule set that actually
/// made the decision over the reconstructed inputs first, and compares what it
/// chooses against what the trace says was chosen. If the two agree, the
/// reconstruction is demonstrably good enough to have produced the original answer,
/// and the comparison against the candidate rules means what it appears to mean. If
/// they disagree, something about the warehouse then is not recoverable now, and
/// saying so per decision is a great deal more honest than a paragraph in a
/// document claiming which decision kinds are exact.
/// </para>
/// </summary>
public enum ReplayFidelity
{
    /// <summary>
    /// The original rules, re-run over the rebuilt inputs, chose what the trace
    /// records. Any difference the candidate rules produce is caused by the rules.
    /// </summary>
    Exact = 1,

    /// <summary>
    /// They did not. Master data has moved, or an input was not recoverable, so the
    /// comparison is indicative rather than conclusive. Kept in the run and counted
    /// separately rather than hidden — a replay that quietly dropped everything
    /// awkward would report a confidence it had not earned.
    /// </summary>
    Approximate = 2,

    /// <summary>
    /// Nothing could be rebuilt — the inputs would not parse, the item is gone, or
    /// the rule set that made the decision no longer exists.
    /// </summary>
    Unreplayable = 3
}

/// <summary>What the candidate rules would have done differently.</summary>
public enum ReplayOutcome
{
    /// <summary>The same choice.</summary>
    Unchanged = 1,

    /// <summary>
    /// The same choice, from a differently ordered field. Worth surfacing rather
    /// than folding into <see cref="Unchanged"/>: a set that agrees today only
    /// because the runner-up was never reached is one delivery away from
    /// disagreeing.
    /// </summary>
    Reordered = 2,

    /// <summary>A different choice.</summary>
    Changed = 3,

    /// <summary>The old rules had no admissible candidate and the new ones do.</summary>
    Gained = 4,

    /// <summary>
    /// The old rules had an answer and the new ones have none. The most expensive
    /// outcome in the list and the reason a run reports it separately: a rule set
    /// that finds nowhere to put a pallet stops the dock.
    /// </summary>
    Lost = 5
}

/// <summary>
/// One historical decision, made again.
/// </summary>
/// <param name="RecordedKey">What the trace says was chosen, at the time, by the rules that ran.</param>
/// <param name="BaselineKey">What those same rules choose now, over the rebuilt inputs. The fidelity check.</param>
/// <param name="CandidateKey">What the candidate rules choose.</param>
/// <param name="CandidateRankUnderBaseline">
/// Where the candidate rules' pick sat in the old ranking. A change from rank 2 is
/// a tuning difference; a change from rank 40 is a different policy, and the number
/// is the cheapest way to tell those apart.
/// <para>
/// This is the number a diff of two stored traces could never supply, and the
/// reason replay re-runs rather than diffs: retention keeps a bounded, round-robin
/// sample of the <i>excluded</i> field, so a stored trace genuinely cannot say
/// where an arbitrary candidate ranked.
/// </para>
/// </param>
public sealed record ReplayedDecision(
    Guid TraceId,
    DateTimeOffset DecidedAt,
    string Subject,
    ReplayFidelity Fidelity,
    string? FidelityNote,
    ReplayOutcome Outcome,
    string? RecordedKey,
    string? BaselineKey,
    string? CandidateKey,
    decimal BaselineScore,
    decimal CandidateScore,
    int CandidateRankUnderBaseline,
    int BaselineRankUnderCandidate,
    int ConsideredCount,
    int BaselineAdmissibleCount,
    int CandidateAdmissibleCount,
    IReadOnlyList<InfluenceLine> BaselineInfluence,
    IReadOnlyList<InfluenceLine> CandidateInfluence,
    long ElapsedMicroseconds);

/// <summary>
/// What one rule was worth across a whole run.
/// </summary>
/// <param name="MeanRealisedShare">
/// The rule's average share of the ranking over every decision replayed. The number
/// that would have caught all three of the scoring flaws found so far: a rule
/// weighted 0.15 that turns out to be doing 90% of the deciding is not a tie-break,
/// whatever the rule set says it is.
/// </param>
/// <param name="Flags">Finding code to the number of decisions that raised it.</param>
public sealed record RuleInfluenceSummary(
    string RuleCode,
    decimal Weight,
    decimal MeanRealisedShare,
    decimal MeanNominalShare,
    decimal MeanValueSpread,
    IReadOnlyDictionary<string, int> Flags);

/// <summary>The whole comparison.</summary>
/// <param name="ExactCount">
/// How many decisions the baseline reproduced. The run's own credibility, reported
/// beside its conclusions rather than left for somebody to ask about.
/// </param>
public sealed record ReplayResult(
    RuleSetStamp Baseline,
    RuleSetStamp Candidate,
    DecisionKind Kind,
    int ReplayedCount,
    int ExactCount,
    int ApproximateCount,
    int UnreplayableCount,
    int ChangedCount,
    int ReorderedCount,
    int GainedCount,
    int LostCount,
    IReadOnlyList<ReplayedDecision> Decisions,
    IReadOnlyList<RuleInfluenceSummary> CandidateInfluence,
    long ElapsedMilliseconds);

/// <summary>Which decisions to replay.</summary>
/// <param name="Limit">
/// Bounded because a replay is interactive. Decision traces carry
/// <c>ElapsedMicroseconds</c>, so the honest budget is measurable rather than
/// guessed: a putaway evaluation over two thousand bins runs in well under a
/// millisecond, and the cost of a run is dominated by rebuilding the warehouse
/// rather than by the rules.
/// </param>
public sealed record ReplayScope(
    Guid WarehouseId,
    DecisionKind Kind,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit = 200);

/// <summary>
/// Rules' published surface for "what would this rule set have done?" (ADR 0004).
/// <para>
/// The half of the explainability promise that only pays off once there is history
/// to replay against, and the reason the trace has been storing its inputs since
/// Phase 3. It is published rather than internal because the module that runs an
/// experiment and keeps its results is not this one — Rules owns the vocabularies
/// and the traces, and knows how to ask a question again; what became of the answer
/// belongs to whoever asked.
/// </para>
/// <para>
/// <b>It re-runs; it does not diff.</b> Two stored traces cannot be compared
/// meaningfully, because <c>TraceRetention</c> keeps a bounded sample of the losing
/// field spread across reasons — a deliberate choice that makes a trace readable and
/// makes "where did that bin rank?" unanswerable from storage. So both rule sets are
/// run again over one reconstruction of the same warehouse, which also means the
/// only difference between the two sides is the rules.
/// </para>
/// </summary>
public interface IShadowReplay
{
    Task<Result<ReplayResult>> RunAsync(
        ReplayScope scope,
        Guid candidateRuleSetId,
        CancellationToken cancellationToken);
}
