namespace Countflow.Modules.Rules.Domain;

/// <summary>
/// Whether a rule decides admissibility or preference. The two halves of the
/// shape ADR 0004 fixes: ordered hard constraints, then weighted soft scorers.
/// </summary>
public enum RuleRole
{
    /// <summary>A hard constraint. Fails, and the candidate is out.</summary>
    Constraint = 1,

    /// <summary>A soft preference. Contributes a weighted score to the ranking.</summary>
    Preference = 2
}

/// <summary>
/// One rule inside a rule set.
/// <para>
/// <b>Why the implementation is code and the configuration is data.</b> A rule
/// row names an implementation by <see cref="Code"/> and supplies its order,
/// weight and parameters. Reordering constraints, retuning weights, disabling a
/// rule or pointing it at different thresholds are all edits to this table.
/// Inventing a genuinely new <i>kind</i> of judgement — "prefer bays under the
/// mezzanine in summer" — needs code, because a rule that could express anything
/// would be a scripting language, and a scripting language cannot be replayed,
/// diffed or reasoned about the way ADR 0004 needs.
/// </para>
/// <para>
/// An unrecognised code is a hard failure at evaluation time, never a skipped
/// rule. Silently dropping a constraint would let a chilled pallet into an
/// ambient aisle because somebody renamed a class.
/// </para>
/// </summary>
public sealed class RuleDefinition
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid RuleSetId { get; init; }

    /// <summary>Stable identifier of the implementation, e.g. <c>putaway.temperature_match</c>.</summary>
    public required string Code { get; set; }

    public required RuleRole Role { get; set; }

    /// <summary>
    /// Evaluation order among constraints. Lower first, and it matters: the first
    /// constraint to fail is the reason the operator is shown, so the cheapest and
    /// most explanatory checks belong at the top.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Relative importance of a preference. Ignored for constraints — a hard
    /// constraint that could be outvoted is not a hard constraint.
    /// </summary>
    public decimal Weight { get; set; }

    /// <summary>Rule-specific settings as JSON. Null means the implementation's defaults.</summary>
    public string? Parameters { get; set; }

    public bool IsEnabled { get; set; } = true;

    public RuleSet? RuleSet { get; init; }
}
