using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Replay.Domain;

/// <summary>
/// One experiment: a candidate rule set, a window of history, and what the
/// comparison found.
/// <para>
/// <b>Why this is a table and not a report.</b> A replay is evidence in an argument
/// about whether to change how the warehouse works, and the argument outlives the
/// screen it was run on. Somebody promotes a rule set on Tuesday and on Friday the
/// picking numbers are worse; the question is what the replay actually said, and
/// "run it again" is not an answer — the window has moved, the warehouse has moved,
/// and a rerun would be a different experiment. So the run is kept, with the
/// fidelity it earned at the time.
/// </para>
/// <para>
/// It is an orchestrator's table in exactly the sense Inbound's, Outbound's and
/// Counting's are. It holds no rule, no trace and no stock figure; every number in
/// it came back from <c>IShadowReplay</c>, and this module could not compute one of
/// them for itself.
/// </para>
/// </summary>
public sealed class ReplayRun
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>Human reference, e.g. <c>RPL-2026-0004</c>. Unique within a warehouse.</summary>
    public required string Reference { get; set; }

    public required DecisionKind Kind { get; init; }

    public required Guid CandidateRuleSetId { get; init; }

    /// <summary>
    /// Copied rather than joined, the same way a trace copies its rule set's name.
    /// A draft gets renamed, promoted and eventually archived, and a run from last
    /// month has to keep saying what it tested.
    /// </summary>
    public required string CandidateRuleSetName { get; init; }

    public required int CandidateRuleSetVersion { get; init; }

    public required Guid BaselineRuleSetId { get; init; }

    public required string BaselineRuleSetName { get; init; }

    public required int BaselineRuleSetVersion { get; init; }

    public required DateTimeOffset FromDate { get; init; }

    public required DateTimeOffset ToDate { get; init; }

    // ---- What it found ----

    public required int ReplayedCount { get; init; }

    /// <summary>
    /// How many decisions the original rules reproduced over the rebuilt inputs.
    /// <para>
    /// The run's own credibility, stored beside its conclusions rather than left to
    /// be inferred. A run that changed four decisions out of two hundred means one
    /// thing when it reproduced all two hundred and something much weaker when it
    /// reproduced sixty.
    /// </para>
    /// </summary>
    public required int ExactCount { get; init; }

    public required int ApproximateCount { get; init; }

    public required int UnreplayableCount { get; init; }

    public required int ChangedCount { get; init; }

    public required int ReorderedCount { get; init; }

    public required int GainedCount { get; init; }

    /// <summary>Decisions the candidate rules could not answer at all. The expensive column.</summary>
    public required int LostCount { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// What each rule was worth across the run, as JSON.
    /// <para>
    /// A blob rather than a table because it is a summary of a summary: nothing
    /// joins to it, nothing filters on it, and its shape follows whatever the
    /// vocabulary contains. The per-decision detail below is the queryable half.
    /// </para>
    /// </summary>
    public required string InfluenceJson { get; init; }

    public required Guid RequestedByUserId { get; init; }

    public required string RequestedByUserName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Notes { get; set; }

    public ICollection<ReplayRunDecision> Decisions { get; init; } = [];
}

/// <summary>
/// One historical decision as the run found it: what was chosen then, what the same
/// rules choose now, and what the candidate rules choose instead.
/// </summary>
public sealed class ReplayRunDecision
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ReplayRunId { get; init; }

    /// <summary>
    /// The trace this replays. Named by id and never joined to — traces are the
    /// <c>rules</c> schema's (ADR 0002), and the console fetches the trace itself
    /// when somebody wants the full reasoning behind either side.
    /// </summary>
    public required Guid DecisionTraceId { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>What the decision was about, e.g. <c>SKU-10001 ×24</c>.</summary>
    public required string Subject { get; init; }

    public required ReplayFidelity Fidelity { get; init; }

    /// <summary>Why the fidelity is what it is, as a code the console renders. Null when there is nothing to say.</summary>
    public string? FidelityNote { get; init; }

    public required ReplayOutcome Outcome { get; init; }

    public string? RecordedKey { get; init; }

    public string? BaselineKey { get; init; }

    public string? CandidateKey { get; init; }

    public required decimal BaselineScore { get; init; }

    public required decimal CandidateScore { get; init; }

    /// <summary>
    /// Where the candidate rules' pick sat in the old ranking.
    /// <para>
    /// The number that separates a tuning difference from a change of policy: a
    /// winner promoted from rank 2 is the same rule set thinking slightly
    /// differently, and one promoted from rank 40 is a different rule set. Zero
    /// means it was not admissible at all under the old rules, which is the loudest
    /// version of the same signal.
    /// </para>
    /// </summary>
    public required int CandidateRankUnderBaseline { get; init; }

    public required int BaselineRankUnderCandidate { get; init; }

    public required int ConsideredCount { get; init; }

    public required int BaselineAdmissibleCount { get; init; }

    public required int CandidateAdmissibleCount { get; init; }

    /// <summary>
    /// What each preference was worth to each side, as JSON. The answer to "why did
    /// it change?", which a pair of winners on its own never gives.
    /// </summary>
    public required string BaselineInfluenceJson { get; init; }

    public required string CandidateInfluenceJson { get; init; }

    public ReplayRun? Run { get; init; }
}
