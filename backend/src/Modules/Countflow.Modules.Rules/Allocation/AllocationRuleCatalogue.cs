using System.Text.Json;
using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Allocation;

public sealed record AllocationRuleSet(
    RuleSetStamp Stamp,
    IReadOnlyList<IConstraintRule<StockHolding, AllocationContext>> Constraints,
    IReadOnlyList<IPreferenceRule<StockHolding, AllocationContext>> Preferences);

/// <summary>
/// The bridge between an allocation rule set stored as rows and the
/// implementations that run.
/// <para>
/// A second catalogue rather than a generic one, and that is deliberate. The two
/// vocabularies are typed against different candidates and different contexts —
/// a putaway rule cannot be put in an allocation set, because it would not
/// compile — and a clever shared registry would have had to erase exactly the
/// type that prevents it. The duplication is a switch statement; what it buys is
/// that a rule row naming <c>putaway.temperature_match</c> in an allocation set
/// is refused rather than quietly ignored.
/// </para>
/// </summary>
public static class AllocationRuleCatalogue
{
    public static readonly IReadOnlyList<string> ConstraintCodes =
    [
        AllocationConstraints.StockAvailable.RuleCode,
        AllocationConstraints.ConditionAllocatable.RuleCode,
        AllocationConstraints.PickableLocation.RuleCode,
        AllocationConstraints.LotMatch.RuleCode,
        AllocationConstraints.NotExpired.RuleCode,
        AllocationConstraints.MinimumRemainingShelfLife.RuleCode
    ];

    public static readonly IReadOnlyList<string> PreferenceCodes =
    [
        AllocationPreferences.FirstExpiredFirstOut.RuleCode,
        AllocationPreferences.ClearTheHolding.RuleCode,
        AllocationPreferences.PreferPickFace.RuleCode,
        AllocationPreferences.TravelDistance.RuleCode
    ];

    public static Result<AllocationRuleSet> Build(RuleSet ruleSet)
    {
        var constraints = new List<IConstraintRule<StockHolding, AllocationContext>>();
        var preferences = new List<IPreferenceRule<StockHolding, AllocationContext>>();

        foreach (var rule in ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            switch (rule.Code)
            {
                case AllocationConstraints.StockAvailable.RuleCode:
                    constraints.Add(new AllocationConstraints.StockAvailable());
                    break;

                case AllocationConstraints.ConditionAllocatable.RuleCode:
                    constraints.Add(new AllocationConstraints.ConditionAllocatable());
                    break;

                case AllocationConstraints.PickableLocation.RuleCode:
                {
                    var parameters = Read<PickableLocationParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<AllocationRuleSet>(parameters.Error);

                    constraints.Add(new AllocationConstraints.PickableLocation(
                        parameters.Value?.Roles is { Count: > 0 } roles
                            ? roles
                            : AllocationConstraints.PickableLocation.DefaultRoles));
                    break;
                }

                case AllocationConstraints.LotMatch.RuleCode:
                    constraints.Add(new AllocationConstraints.LotMatch());
                    break;

                case AllocationConstraints.NotExpired.RuleCode:
                    constraints.Add(new AllocationConstraints.NotExpired());
                    break;

                case AllocationConstraints.MinimumRemainingShelfLife.RuleCode:
                    constraints.Add(new AllocationConstraints.MinimumRemainingShelfLife());
                    break;

                case AllocationPreferences.FirstExpiredFirstOut.RuleCode:
                    preferences.Add(new AllocationPreferences.FirstExpiredFirstOut(rule.Weight));
                    break;

                case AllocationPreferences.ClearTheHolding.RuleCode:
                    preferences.Add(new AllocationPreferences.ClearTheHolding(rule.Weight));
                    break;

                case AllocationPreferences.PreferPickFace.RuleCode:
                    preferences.Add(new AllocationPreferences.PreferPickFace(rule.Weight));
                    break;

                case AllocationPreferences.TravelDistance.RuleCode:
                    preferences.Add(new AllocationPreferences.TravelDistance(rule.Weight));
                    break;

                default:
                    // Fatal, never skipped — the same rule as putaway, and the
                    // stakes are if anything higher: a dropped shelf-life
                    // constraint ships short-dated goods to a customer who has
                    // contracted not to receive them, and nothing in the warehouse
                    // notices.
                    return Result.Failure<AllocationRuleSet>(RulesErrors.UnknownRule(rule.Code));
            }
        }

        return new AllocationRuleSet(
            new RuleSetStamp(ruleSet.Id, ruleSet.Name, ruleSet.Version),
            constraints,
            preferences);
    }

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

    private sealed record PickableLocationParameters(IReadOnlyList<StorageRole>? Roles);
}
