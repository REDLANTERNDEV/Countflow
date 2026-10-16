using Countflow.Modules.Rules.Domain;
using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Putaway;

public sealed record PalletPutawayRuleSet(
    RuleSetStamp Stamp,
    IReadOnlyList<IConstraintRule<StorageLocationSnapshot, PalletPutawayContext>> Constraints,
    IReadOnlyList<IPreferenceRule<StorageLocationSnapshot, PalletPutawayContext>> Preferences);

/// <summary>The existing putaway vocabulary, applied to one whole load.</summary>
public static class PalletPutawayRuleCatalogue
{
    public static Result<PalletPutawayRuleSet> Build(RuleSet ruleSet)
    {
        var single = PutawayRuleCatalogue.Build(ruleSet);
        if (single.IsFailure) return Result.Failure<PalletPutawayRuleSet>(single.Error);
        return new PalletPutawayRuleSet(single.Value.Stamp,
            [.. single.Value.Constraints.Select(rule => new Constraint(rule))],
            [.. single.Value.Preferences.Select(rule => new Preference(rule))]);
    }

    private sealed class Constraint(IConstraintRule<StorageLocationSnapshot, PutawayContext> rule)
        : IConstraintRule<StorageLocationSnapshot, PalletPutawayContext>
    {
        public string Code => rule.Code;
        public RuleNote? Exclude(StorageLocationSnapshot candidate, PalletPutawayContext context)
        {
            if (context.Lines.Count == 1) return rule.Exclude(candidate, context.Lines[0]);
            switch (Code)
            {
                case PutawayConstraints.LocationActive.RuleCode:
                case PutawayConstraints.StorableLocation.RuleCode:
                case PutawayConstraints.VolumeCapacity.RuleCode:
                case PutawayConstraints.WeightCapacity.RuleCode:
                    return rule.Exclude(candidate, context.WholeLoad);

                case PutawayConstraints.TemperatureMatch.RuleCode:
                    return FirstExclusion(candidate, context);

                case PutawayConstraints.LotPurity.RuleCode:
                    var mixed = context.Lines.Where(line => line.Item.IsLotTracked)
                        .GroupBy(line => line.Item.Id)
                        .FirstOrDefault(group => group.Select(line => line.LotId).Distinct().Skip(1).Any());
                    return mixed is null ? FirstExclusion(candidate, context)
                        : RuleNote.Of("rules.putaway.pallet_lot_commingling", ("sku", mixed.First().Item.Sku));

                case PutawayConstraints.DistinctItemLimit.RuleCode:
                    if (candidate.MaxDistinctItems is not { } limit) return null;
                    var held = context.WholeLoad.At(candidate.Id)?.DistinctItems ?? 0;
                    var added = context.Lines.GroupBy(line => line.Item.Id)
                        .Count(group => !group.Any(line => line.At(candidate.Id) is { SubjectItemUnits: > 0m }));
                    return held + added > limit ? RuleNote.Of("rules.putaway.pallet_distinct_item_limit",
                        ("limit", limit.ToString(CultureInfo.InvariantCulture)),
                        ("incoming", added.ToString(CultureInfo.InvariantCulture)),
                        ("held", held.ToString(CultureInfo.InvariantCulture))) : null;

                default:
                    throw new InvalidOperationException($"Putaway rule '{Code}' has no whole-load semantics.");
            }
        }

        private RuleNote? FirstExclusion(StorageLocationSnapshot candidate, PalletPutawayContext context)
        {
            foreach (var line in context.Lines)
                if (rule.Exclude(candidate, line) is { } excluded) return excluded;
            return null;
        }
    }

    private sealed class Preference(IPreferenceRule<StorageLocationSnapshot, PutawayContext> rule)
        : IPreferenceRule<StorageLocationSnapshot, PalletPutawayContext>
    {
        public string Code => rule.Code;
        public decimal Weight => rule.Weight;
        public ScoreContribution Score(StorageLocationSnapshot candidate, PalletPutawayContext context)
        {
            if (context.Lines.Count == 1) return rule.Score(candidate, context.Lines[0]);
            switch (Code)
            {
                case PutawayPreferences.TravelDistance.RuleCode:
                case PutawayPreferences.CubeUtilisation.RuleCode:
                    return rule.Score(candidate, context.WholeLoad);

                case PutawayPreferences.ConsolidateSameItem.RuleCode:
                case PutawayPreferences.VelocitySlotting.RuleCode:
                    // Base units of different SKUs are incomparable. Give each
                    // SKU equal influence; within a SKU weight lots by units so
                    // splitting one delivery into lines cannot change its vote.
                    var scores = context.Lines.GroupBy(line => line.Item.Id).Select(group =>
                    {
                        var units = group.Sum(line => line.Quantity);
                        return (Sku: group.First().Item.Sku,
                            Value: group.Sum(line => line.Quantity * rule.Score(candidate, line).Value) / units);
                    }).ToArray();
                    var average = scores.Average(score => score.Value);
                    return ScoreContribution.Of(average,
                        Code == PutawayPreferences.ConsolidateSameItem.RuleCode
                            ? "rules.putaway.pallet_consolidation" : "rules.putaway.pallet_velocity",
                        ("items", scores.Length.ToString(CultureInfo.InvariantCulture)),
                        ("percent", Percent(average)),
                        ("scores", string.Join(", ", scores.Select(score => $"{score.Sku}: {Percent(score.Value)}%"))));

                default:
                    throw new InvalidOperationException($"Putaway rule '{Code}' has no whole-load semantics.");
            }
        }

        private static string Percent(decimal score) =>
            Math.Round(score * 100m, 3).ToString(CultureInfo.InvariantCulture);
    }
}
