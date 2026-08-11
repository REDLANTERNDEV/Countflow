using System.Text.Json;
using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// The bridge between a rule set stored as rows and the implementations that run.
/// <para>
/// This is where "rules are data" stops and "rule kinds are code" starts, and the
/// seam is deliberately narrow: a row supplies order, weight, enablement and
/// parameters; the code named by <see cref="RuleDefinition.Code"/> supplies the
/// judgement. Anything a warehouse actually tunes lives on the row.
/// </para>
/// </summary>
public sealed record PutawayRuleSet(
    RuleSetStamp Stamp,
    IReadOnlyList<IConstraintRule<StorageLocationSnapshot, PutawayContext>> Constraints,
    IReadOnlyList<IPreferenceRule<StorageLocationSnapshot, PutawayContext>> Preferences);

public static class PutawayRuleCatalogue
{
    /// <summary>
    /// Every implementation this build can run. Published because the console
    /// lists it — a supervisor editing a rule set has to be able to see what is
    /// available, not guess at codes.
    /// </summary>
    public static readonly IReadOnlyList<string> ConstraintCodes =
    [
        PutawayConstraints.LocationActive.RuleCode,
        PutawayConstraints.StorableLocation.RuleCode,
        PutawayConstraints.TemperatureMatch.RuleCode,
        PutawayConstraints.LotPurity.RuleCode,
        PutawayConstraints.DistinctItemLimit.RuleCode,
        PutawayConstraints.VolumeCapacity.RuleCode,
        PutawayConstraints.WeightCapacity.RuleCode
    ];

    public static readonly IReadOnlyList<string> PreferenceCodes =
    [
        PutawayPreferences.ConsolidateSameItem.RuleCode,
        PutawayPreferences.VelocitySlotting.RuleCode,
        PutawayPreferences.TravelDistance.RuleCode,
        PutawayPreferences.CubeUtilisation.RuleCode
    ];

    public static Result<PutawayRuleSet> Build(RuleSet ruleSet)
    {
        var constraints = new List<IConstraintRule<StorageLocationSnapshot, PutawayContext>>();
        var preferences = new List<IPreferenceRule<StorageLocationSnapshot, PutawayContext>>();

        foreach (var rule in ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            switch (rule.Code)
            {
                case PutawayConstraints.LocationActive.RuleCode:
                    constraints.Add(new PutawayConstraints.LocationActive());
                    break;

                case PutawayConstraints.StorableLocation.RuleCode:
                {
                    var parameters = Read<StorableLocationParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<PutawayRuleSet>(parameters.Error);

                    constraints.Add(new PutawayConstraints.StorableLocation(
                        parameters.Value?.Roles is { Count: > 0 } roles
                            ? roles
                            : PutawayConstraints.StorableLocation.DefaultRoles));
                    break;
                }

                case PutawayConstraints.TemperatureMatch.RuleCode:
                    constraints.Add(new PutawayConstraints.TemperatureMatch());
                    break;

                case PutawayConstraints.LotPurity.RuleCode:
                    constraints.Add(new PutawayConstraints.LotPurity());
                    break;

                case PutawayConstraints.DistinctItemLimit.RuleCode:
                    constraints.Add(new PutawayConstraints.DistinctItemLimit());
                    break;

                case PutawayConstraints.VolumeCapacity.RuleCode:
                    constraints.Add(new PutawayConstraints.VolumeCapacity());
                    break;

                case PutawayConstraints.WeightCapacity.RuleCode:
                    constraints.Add(new PutawayConstraints.WeightCapacity());
                    break;

                case PutawayPreferences.ConsolidateSameItem.RuleCode:
                    preferences.Add(new PutawayPreferences.ConsolidateSameItem(rule.Weight));
                    break;

                case PutawayPreferences.VelocitySlotting.RuleCode:
                    preferences.Add(new PutawayPreferences.VelocitySlotting(rule.Weight));
                    break;

                case PutawayPreferences.TravelDistance.RuleCode:
                    preferences.Add(new PutawayPreferences.TravelDistance(rule.Weight));
                    break;

                case PutawayPreferences.CubeUtilisation.RuleCode:
                    preferences.Add(new PutawayPreferences.CubeUtilisation(rule.Weight));
                    break;

                default:
                    return Result.Failure<PutawayRuleSet>(RulesErrors.UnknownRule(rule.Code));
            }
        }

        return new PutawayRuleSet(
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
            // A malformed parameter blob stops the evaluation rather than falling
            // back to defaults. Silently running a constraint with settings nobody
            // chose is worse than refusing to run at all, because it looks like it
            // worked.
            return Result.Failure<TParameters?>(
                RulesErrors.InvalidRuleParameters(rule.Code, exception.Message));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record StorableLocationParameters(IReadOnlyList<StorageRole>? Roles);
}
