using System.Text.Json;
using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Dispositions;

/// <summary>
/// The bridge between a disposition rule set stored as rows and the
/// implementations that run.
/// <para>
/// The fourth of these, and identical in shape to the other three — which is the
/// point of writing it down here rather than in a changelog. Decision 11 claimed a
/// new vocabulary would need no change to the kernel; the evidence is that this
/// file is the same file as <c>PutawayRuleCatalogue</c> with different rule names
/// in it, and that <c>RuleEngine</c>, <c>Decision&lt;T&gt;</c>,
/// <c>DecisionJournal</c> and the trace schema were not opened to add it.
/// </para>
/// <para>
/// The price is the same price the invariants already name: a second catalogue,
/// which is to say a switch statement. It is the right price, and this is the
/// fourth time it has been paid.
/// </para>
/// </summary>
public sealed record DispositionRuleSet(
    RuleSetStamp Stamp,
    IReadOnlyList<IConstraintRule<DispositionOption, DispositionContext>> Constraints,
    IReadOnlyList<IPreferenceRule<DispositionOption, DispositionContext>> Preferences);

public static class DispositionRuleCatalogue
{
    public static readonly IReadOnlyList<string> ConstraintCodes =
    [
        DispositionConstraints.RestockNeedsSaleableStock.RuleCode,
        DispositionConstraints.ShelfLifeRemaining.RuleCode,
        DispositionConstraints.RepairNeedsARoute.RuleCode,
        DispositionConstraints.ScrapIsNotForSaleableStock.RuleCode,
        DispositionConstraints.HazardousNeverRestocked.RuleCode
    ];

    public static readonly IReadOnlyList<string> PreferenceCodes =
    [
        DispositionPreferences.RecoverValue.RuleCode,
        DispositionPreferences.HandlingCost.RuleCode,
        DispositionPreferences.ReturnFreshness.RuleCode,
        DispositionPreferences.ShelfLifeHeadroom.RuleCode
    ];

    public static Result<DispositionRuleSet> Build(RuleSet ruleSet)
    {
        var constraints = new List<IConstraintRule<DispositionOption, DispositionContext>>();
        var preferences = new List<IPreferenceRule<DispositionOption, DispositionContext>>();

        foreach (var rule in ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            switch (rule.Code)
            {
                case DispositionConstraints.RestockNeedsSaleableStock.RuleCode:
                    constraints.Add(new DispositionConstraints.RestockNeedsSaleableStock());
                    break;

                case DispositionConstraints.ShelfLifeRemaining.RuleCode:
                    constraints.Add(new DispositionConstraints.ShelfLifeRemaining());
                    break;

                case DispositionConstraints.RepairNeedsARoute.RuleCode:
                    constraints.Add(new DispositionConstraints.RepairNeedsARoute());
                    break;

                case DispositionConstraints.ScrapIsNotForSaleableStock.RuleCode:
                    constraints.Add(new DispositionConstraints.ScrapIsNotForSaleableStock());
                    break;

                case DispositionConstraints.HazardousNeverRestocked.RuleCode:
                    constraints.Add(new DispositionConstraints.HazardousNeverRestocked());
                    break;

                case DispositionPreferences.RecoverValue.RuleCode:
                    preferences.Add(new DispositionPreferences.RecoverValue(rule.Weight));
                    break;

                case DispositionPreferences.HandlingCost.RuleCode:
                    preferences.Add(new DispositionPreferences.HandlingCost(rule.Weight));
                    break;

                case DispositionPreferences.ReturnFreshness.RuleCode:
                {
                    var parameters = Read<FreshnessParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<DispositionRuleSet>(parameters.Error);

                    preferences.Add(new DispositionPreferences.ReturnFreshness(
                        rule.Weight,
                        parameters.Value?.WindowDays is > 0 and { } days
                            ? days
                            : DispositionPreferences.ReturnFreshness.DefaultWindowDays));
                    break;
                }

                case DispositionPreferences.ShelfLifeHeadroom.RuleCode:
                    preferences.Add(new DispositionPreferences.ShelfLifeHeadroom(rule.Weight));
                    break;

                default:
                    return Result.Failure<DispositionRuleSet>(RulesErrors.UnknownRule(rule.Code));
            }
        }

        return new DispositionRuleSet(
            new RuleSetStamp(ruleSet.Id, ruleSet.Name, ruleSet.Version),
            constraints,
            preferences);
    }

    /// <summary>
    /// Same reader as the other catalogues, and the same refusal to fall back to
    /// defaults on a malformed blob: running a rule with settings nobody chose is
    /// worse than not running at all, because it looks like it worked.
    /// </summary>
    private static Result<TParameters?> Read<TParameters>(RuleDefinition rule)
        where TParameters : class
    {
        if (string.IsNullOrWhiteSpace(rule.Parameters))
        {
            return Result.Success<TParameters?>(null);
        }

        try
        {
            return JsonSerializer.Deserialize<TParameters>(rule.Parameters, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result.Failure<TParameters?>(
                RulesErrors.InvalidRuleParameters(rule.Code, exception.Message));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record FreshnessParameters(int? WindowDays);
}
