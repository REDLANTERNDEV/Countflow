using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.CycleCount;
using Countflow.Modules.Rules.Dispositions;
using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Features.RuleSets;

/// <summary>
/// What each decision kind's rule set may contain, and whether a given one is
/// usable.
/// <para>
/// The one place the four catalogues are addressed generically, and it is
/// deliberately the *only* one. Everywhere a rule actually runs, the vocabulary
/// stays typed against its own candidate and context — a putaway rule is written
/// about locations and an allocation rule about holdings, and neither can appear
/// in the other's set because neither would compile there. That guarantee is worth
/// keeping, so this switch erases the types for exactly two purposes: listing what
/// a supervisor may add, and answering "would this set run?".
/// </para>
/// </summary>
public static class RuleVocabulary
{
    public static IReadOnlyList<DecisionKind> Kinds { get; } =
        [DecisionKind.Putaway, DecisionKind.Allocation, DecisionKind.CycleCount, DecisionKind.Disposition];

    public static IReadOnlyList<string> ConstraintCodes(DecisionKind kind) => kind switch
    {
        DecisionKind.Putaway => PutawayRuleCatalogue.ConstraintCodes,
        DecisionKind.Allocation => AllocationRuleCatalogue.ConstraintCodes,
        DecisionKind.CycleCount => CountRuleCatalogue.ConstraintCodes,
        DecisionKind.Disposition => DispositionRuleCatalogue.ConstraintCodes,
        _ => []
    };

    public static IReadOnlyList<string> PreferenceCodes(DecisionKind kind) => kind switch
    {
        DecisionKind.Putaway => PutawayRuleCatalogue.PreferenceCodes,
        DecisionKind.Allocation => AllocationRuleCatalogue.PreferenceCodes,
        DecisionKind.CycleCount => CountRuleCatalogue.PreferenceCodes,
        DecisionKind.Disposition => DispositionRuleCatalogue.PreferenceCodes,
        _ => []
    };

    public static bool HasVocabulary(DecisionKind kind) => ConstraintCodes(kind).Count > 0;

    /// <summary>
    /// Whether a rule set would run, checked by <b>building it</b> rather than by a
    /// second set of validation rules written alongside the catalogue.
    /// <para>
    /// This is the whole of the editor's safety, and it is safety worth having:
    /// every rule set in this system is loaded, built and evaluated on the critical
    /// path of a pallet arriving at a dock, and a set that fails to build fails
    /// there — at the rack, in front of an operator, with a receipt half done.
    /// Building it at the moment somebody presses save moves that failure to the
    /// only place it can be dealt with calmly. And because it is the real catalogue
    /// rather than a copy of its rules, a validator cannot drift into disagreeing
    /// with what will actually run.
    /// </para>
    /// </summary>
    public static Result Builds(RuleSet ruleSet)
    {
        var built = ruleSet.Kind switch
        {
            DecisionKind.Putaway => (Result)PutawayRuleCatalogue.Build(ruleSet),
            DecisionKind.Allocation => AllocationRuleCatalogue.Build(ruleSet),
            DecisionKind.CycleCount => CountRuleCatalogue.Build(ruleSet),
            DecisionKind.Disposition => DispositionRuleCatalogue.Build(ruleSet),
            _ => Result.Failure(RulesErrors.NoVocabulary(ruleSet.Kind.ToString()))
        };

        return built.IsFailure ? Result.Failure(built.Error) : Result.Success();
    }
}
