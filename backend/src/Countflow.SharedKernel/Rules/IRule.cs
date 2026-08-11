namespace Countflow.SharedKernel.Rules;

/// <summary>
/// A hard constraint. Either the candidate is admissible or it is out, and being
/// out always comes with a reason.
/// <para>
/// Modelled as "return the reason it fails, or null" rather than as a predicate
/// plus a separate message lookup: a boolean rule and its explanation kept in two
/// places is how the explanation eventually stops matching the rule, which is
/// precisely the rot ADR 0004 exists to prevent.
/// </para>
/// </summary>
public interface IConstraintRule<in TCandidate, in TContext>
{
    /// <summary>Stable identifier, e.g. <c>putaway.temperature_match</c>. Persisted in traces, so it is a contract.</summary>
    string Code { get; }

    /// <summary>Null when the candidate satisfies the constraint; otherwise why it does not.</summary>
    RuleNote? Exclude(TCandidate candidate, TContext context);
}

/// <summary>
/// A soft preference. Every admissible candidate gets a score, and the weighted
/// sum ranks them.
/// </summary>
public interface IPreferenceRule<in TCandidate, in TContext>
{
    string Code { get; }

    /// <summary>
    /// How much this preference counts relative to the others. Data, not code —
    /// tuning a warehouse means changing weights, and that must not be a deploy.
    /// </summary>
    decimal Weight { get; }

    ScoreContribution Score(TCandidate candidate, TContext context);
}

/// <summary>
/// One preference's opinion of one candidate.
/// </summary>
/// <param name="Value">
/// Normalised to <c>[0, 1]</c>, where 1 is "ideal by this preference alone". The
/// engine multiplies it by the rule's weight, and normalising here is what makes
/// weights comparable across rules that measure entirely different things —
/// metres of travel against cubic metres of free space.
/// </param>
public sealed record ScoreContribution(decimal Value, RuleNote Reason)
{
    public static ScoreContribution Of(decimal value, string code) =>
        new(value, RuleNote.Of(code));

    public static ScoreContribution Of(decimal value, string code, params (string Key, string Value)[] args) =>
        new(value, RuleNote.Of(code, args));
}
