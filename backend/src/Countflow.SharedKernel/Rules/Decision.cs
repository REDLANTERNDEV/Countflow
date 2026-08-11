namespace Countflow.SharedKernel.Rules;

/// <summary>
/// What one preference rule contributed to one candidate's score.
/// </summary>
/// <param name="Contribution"><c>Weight × Value</c>, pre-computed so a trace can be read without re-doing the arithmetic.</param>
public sealed record ScoreLine(
    string RuleCode,
    decimal Weight,
    decimal Value,
    decimal Contribution,
    RuleNote Reason);

/// <summary>
/// One candidate, and everything the rule set had to say about it.
/// </summary>
/// <param name="Rank">
/// 1-based position among the admissible candidates; 0 for an excluded one.
/// </param>
/// <param name="ExcludedBy">
/// The constraint that ruled it out, or null if it survived. Only ever one:
/// evaluation stops at the first failure, because "chilled goods, ambient aisle"
/// is the answer an operator needs and a list of six further consequences of the
/// same mismatch is not.
/// </param>
public sealed record CandidateEvaluation<TCandidate>(
    TCandidate Candidate,
    int Rank,
    bool IsAdmissible,
    decimal Score,
    RuleNote? ExcludedBy,
    IReadOnlyList<ScoreLine> Scores)
    where TCandidate : IRuleCandidate;

/// <summary>
/// The result of a rule evaluation — <b>and</b> its full trace, because they are
/// the same object.
/// <para>
/// This is ADR 0004 expressed in the type system. There is no method anywhere
/// that hands back a winner on its own: to learn what the rules chose you must
/// hold one of these, and holding one means holding every candidate that was
/// considered, the constraint that excluded each one that failed, and the scored
/// breakdown behind the ranking of each one that did not. Explainability cannot
/// rot as rules accumulate, because there is no cheaper path to route around.
/// </para>
/// <para>
/// The constructor is deliberately <c>internal</c>. <see cref="RuleEngine"/> is
/// the only thing that can produce a decision, so a caller cannot assemble a
/// plausible-looking one by hand and skip the evaluation that was supposed to
/// justify it.
/// </para>
/// </summary>
public sealed class Decision<TCandidate>
    where TCandidate : IRuleCandidate
{
    internal Decision(
        DecisionKind kind,
        RuleSetStamp ruleSet,
        IReadOnlyList<CandidateEvaluation<TCandidate>> candidates,
        int consideredCount,
        int admissibleCount,
        TimeSpan elapsed)
    {
        Kind = kind;
        RuleSet = ruleSet;
        Candidates = candidates;
        ConsideredCount = consideredCount;
        AdmissibleCount = admissibleCount;
        Elapsed = elapsed;
    }

    public DecisionKind Kind { get; }

    public RuleSetStamp RuleSet { get; }

    /// <summary>
    /// The retained trace: every admissible candidate in rank order, then a
    /// bounded sample of the excluded ones.
    /// <para>
    /// Bounded because a putaway decision considers hundreds of bins and almost
    /// all of them fail the same constraint. Keeping every one would store the
    /// same sentence three hundred times per pallet, and ADR 0004 already warns
    /// that traces need a retention policy. <see cref="ConsideredCount"/> is
    /// exact regardless of what was kept, so the trace never overstates how much
    /// of the warehouse it looked at.
    /// </para>
    /// </summary>
    public IReadOnlyList<CandidateEvaluation<TCandidate>> Candidates { get; }

    /// <summary>Candidates offered to the rule set, including any dropped from the retained trace.</summary>
    public int ConsideredCount { get; }

    public int AdmissibleCount { get; }

    /// <summary>How long the evaluation took. High-volume decision types are where a sampling policy will eventually bite.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The chosen candidate, or null when every candidate was excluded.
    /// <para>
    /// Null is a real and unremarkable answer — a full chilled aisle has no
    /// admissible location — and it is why this is not an exception. The caller
    /// still holds the trace, so it can tell the operator <i>why</i> nothing was
    /// suggested, which is the only useful thing to say at that point.
    /// </para>
    /// </summary>
    public CandidateEvaluation<TCandidate>? Winner =>
        Candidates.Count > 0 && Candidates[0].IsAdmissible ? Candidates[0] : null;
}
