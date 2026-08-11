using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Domain;

/// <summary>
/// A rule evaluation, kept.
/// <para>
/// Written before the work it justifies is created, so a movement or a task can
/// carry its identifier. A trace is therefore allowed to exist with nothing
/// pointing at it — a preview, or a decision whose posting then failed — but the
/// reverse is not allowed to happen, because a movement pointing at a trace that
/// was never written looks like an explanation and is not one.
/// </para>
/// <para>
/// Never updated. Same argument as the ledger (ADR 0001), for the same reason: an
/// explanation that can be revised after the fact explains nothing. Unlike the
/// ledger this is not enforced with a trigger — a trace is evidence about a
/// decision rather than the record of a stock position, and the cost of losing
/// one is an unanswered question rather than a wrong inventory figure.
/// </para>
/// </summary>
public sealed class DecisionTrace
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DecisionKind Kind { get; init; }

    public required Guid RuleSetId { get; init; }

    /// <summary>
    /// Copied, not joined. Same argument as <c>StockMovement.UserName</c>: the
    /// rule set will be renamed and superseded, and a trace from 2026 has to keep
    /// saying which rules ran.
    /// </summary>
    public required string RuleSetName { get; init; }

    public required int RuleSetVersion { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    public required int ConsideredCount { get; init; }

    public required int AdmissibleCount { get; init; }

    /// <summary>Microseconds, not milliseconds: a good putaway evaluation is well under one.</summary>
    public required long ElapsedMicroseconds { get; init; }

    public Guid? SelectedCandidateId { get; init; }

    public string? SelectedCandidateKey { get; init; }

    /// <summary>
    /// The decision's inputs, as JSON.
    /// <para>
    /// This is the half of ADR 0004 that only pays off in Phase 6. Shadow replay
    /// needs decision <i>inputs</i> reconstructible from history, not just
    /// outcomes — so what was asked is stored alongside what was answered. The
    /// warehouse state the rules saw is not stored, because the ledger already is
    /// one: occupancy as of <see cref="DecidedAt"/> is replayable exactly, which
    /// is the second place the immutable ledger pays for itself.
    /// </para>
    /// </summary>
    public required string InputsJson { get; init; }

    /// <summary>
    /// True when nobody acted on this — a supervisor asking "where would you put
    /// it?". Kept in the same table so previews and live decisions are directly
    /// comparable, flagged so the accuracy statistics do not count questions as
    /// decisions.
    /// </summary>
    public bool IsPreview { get; init; }

    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public ICollection<DecisionTraceCandidate> Candidates { get; init; } = [];
}

/// <summary>
/// One candidate as the rules saw it. The row that answers "why not that bin?".
/// </summary>
public sealed class DecisionTraceCandidate
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid DecisionTraceId { get; init; }

    public required Guid CandidateId { get; init; }

    /// <summary>The rack label. A trace showing GUIDs explains nothing to the person holding the pallet.</summary>
    public required string CandidateKey { get; init; }

    /// <summary>1-based among admissible candidates; 0 for an excluded one.</summary>
    public required int Rank { get; init; }

    public required bool IsAdmissible { get; init; }

    public required decimal Score { get; init; }

    /// <summary>The constraint that ruled it out, as a serialised <see cref="RuleNote"/>. Null when it survived.</summary>
    public string? ExcludedByJson { get; init; }

    /// <summary>The weighted breakdown behind <see cref="Score"/>, as serialised <see cref="ScoreLine"/>s.</summary>
    public required string ScoresJson { get; init; }

    public DecisionTrace? Trace { get; init; }
}
