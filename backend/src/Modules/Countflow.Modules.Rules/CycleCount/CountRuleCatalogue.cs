using System.Text.Json;
using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.CycleCount;

public sealed record CountRuleSet(
    RuleSetStamp Stamp,
    IReadOnlyList<IConstraintRule<CountCandidate, CountContext>> Constraints,
    IReadOnlyList<IPreferenceRule<CountCandidate, CountContext>> Preferences);

/// <summary>
/// The bridge between a counting rule set stored as rows and the implementations
/// that run.
/// <para>
/// The third of these, and the point at which the duplication is worth restating
/// rather than apologising for. A generic registry would have to erase the very
/// types that make a putaway rule impossible to put in a counting set — the
/// candidate and the context — and what the three switch statements buy is that a
/// rule row naming <c>allocation.fefo</c> in a counting set is refused at
/// evaluation rather than quietly ignored at the moment it would have mattered.
/// </para>
/// <para>
/// It is also the measurement the phase was partly for. Adding this vocabulary was
/// a context, a constraints file, a preferences file, this switch and a seeder —
/// no change to the engine, the journal, the trace schema, the preview endpoint's
/// shape or the console's trace component. That is the afternoon ADR 0004 promised.
/// </para>
/// </summary>
public static class CountRuleCatalogue
{
    public static readonly IReadOnlyList<string> ConstraintCodes =
    [
        CountConstraints.CountableLocation.RuleCode,
        CountConstraints.SettledStock.RuleCode,
        CountConstraints.NotAlreadyScheduled.RuleCode,
        CountConstraints.NotRecentlyCounted.RuleCode
    ];

    public static readonly IReadOnlyList<string> PreferenceCodes =
    [
        CountPreferences.Overdue.RuleCode,
        CountPreferences.Velocity.RuleCode,
        CountPreferences.StockAtRisk.RuleCode,
        CountPreferences.TravelDistance.RuleCode
    ];

    public static Result<CountRuleSet> Build(RuleSet ruleSet)
    {
        var constraints = new List<IConstraintRule<CountCandidate, CountContext>>();
        var preferences = new List<IPreferenceRule<CountCandidate, CountContext>>();

        foreach (var rule in ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            switch (rule.Code)
            {
                case CountConstraints.CountableLocation.RuleCode:
                {
                    var parameters = Read<CountableLocationParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<CountRuleSet>(parameters.Error);

                    constraints.Add(new CountConstraints.CountableLocation(
                        parameters.Value?.Roles is { Count: > 0 } roles
                            ? roles
                            : CountConstraints.CountableLocation.DefaultRoles));
                    break;
                }

                case CountConstraints.SettledStock.RuleCode:
                {
                    var parameters = Read<SettledStockParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<CountRuleSet>(parameters.Error);

                    constraints.Add(new CountConstraints.SettledStock(
                        parameters.Value?.QuietPeriodHours
                        ?? CountConstraints.SettledStock.DefaultQuietPeriodHours));
                    break;
                }

                case CountConstraints.NotAlreadyScheduled.RuleCode:
                    constraints.Add(new CountConstraints.NotAlreadyScheduled());
                    break;

                case CountConstraints.NotRecentlyCounted.RuleCode:
                {
                    var parameters = Read<NotRecentlyCountedParameters>(rule);
                    if (parameters.IsFailure) return Result.Failure<CountRuleSet>(parameters.Error);

                    constraints.Add(new CountConstraints.NotRecentlyCounted(
                        parameters.Value?.MinimumDaysBetweenCounts
                        ?? CountConstraints.NotRecentlyCounted.DefaultMinimumDays));
                    break;
                }

                case CountPreferences.Overdue.RuleCode:
                {
                    var parameters = Read<CountIntervals>(rule);
                    if (parameters.IsFailure) return Result.Failure<CountRuleSet>(parameters.Error);

                    preferences.Add(new CountPreferences.Overdue(
                        rule.Weight, parameters.Value ?? CountIntervals.Default));
                    break;
                }

                case CountPreferences.Velocity.RuleCode:
                    preferences.Add(new CountPreferences.Velocity(rule.Weight));
                    break;

                case CountPreferences.StockAtRisk.RuleCode:
                    preferences.Add(new CountPreferences.StockAtRisk(rule.Weight));
                    break;

                case CountPreferences.TravelDistance.RuleCode:
                    preferences.Add(new CountPreferences.TravelDistance(rule.Weight));
                    break;

                default:
                    // Fatal, never skipped — the same rule as the other two
                    // catalogues, though the failure it prevents here is quieter and
                    // slower. A dropped counting constraint does not send a pallet
                    // to the wrong aisle; it sends counters to bays that are being
                    // worked, which manufactures variances and teaches everybody
                    // that the counts are noise.
                    return Result.Failure<CountRuleSet>(RulesErrors.UnknownRule(rule.Code));
            }
        }

        return new CountRuleSet(
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

    private sealed record CountableLocationParameters(IReadOnlyList<StorageRole>? Roles);

    private sealed record SettledStockParameters(int? QuietPeriodHours);

    private sealed record NotRecentlyCountedParameters(int? MinimumDaysBetweenCounts);
}
