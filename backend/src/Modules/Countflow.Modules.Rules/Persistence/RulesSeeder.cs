using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Modules;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Persistence;

/// <summary>
/// Seeds the rule set the warehouse runs on out of the box.
/// <para>
/// Not gated behind <c>Seed:DemoData</c>, unlike the sample warehouse. A rule set
/// is reference data the system cannot function without — an empty
/// <c>rules</c> schema means every putaway fails — so it is seeded on every
/// deploy, in the migrator, exactly once.
/// </para>
/// <para>
/// The weights below are a defensible starting point, not a claim to be optimal.
/// They are data precisely so the first supervisor who watches the system for a
/// week can change them without waiting for a release, and Phase 6 exists to tell
/// them whether the change helped.
/// </para>
/// </summary>
public sealed class RulesSeeder(RulesDbContext dbContext, ILogger<RulesSeeder> logger) : IModuleSeeder
{
    /// <summary>After master data, whose locations the rules are written about.</summary>
    public int Order => 20;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.RuleSets.AnyAsync(r => r.Kind == DecisionKind.Putaway, cancellationToken))
        {
            logger.LogInformation("Putaway rule set already present; skipping seed");
            return;
        }

        var ruleSet = new RuleSet
        {
            Kind = DecisionKind.Putaway,
            Name = "Standard putaway",
            Version = 1,
            Status = RuleSetStatus.Active,
            WarehouseId = null,
            ActivatedAt = DateTimeOffset.UtcNow,
            Notes = "Shipped default. Constraints ordered cheapest and most explanatory first."
        };

        dbContext.RuleSets.Add(ruleSet);

        // Constraint order is the order an operator reads. Temperature comes
        // before capacity because "chilled goods, ambient aisle" is a better
        // answer than "not enough room", even when both are true.
        Constraint(ruleSet, PutawayConstraints.LocationActive.RuleCode, order: 10);
        Constraint(ruleSet, PutawayConstraints.StorableLocation.RuleCode, order: 20);
        Constraint(ruleSet, PutawayConstraints.TemperatureMatch.RuleCode, order: 30);
        Constraint(ruleSet, PutawayConstraints.LotPurity.RuleCode, order: 40);
        Constraint(ruleSet, PutawayConstraints.DistinctItemLimit.RuleCode, order: 50);
        Constraint(ruleSet, PutawayConstraints.VolumeCapacity.RuleCode, order: 60);
        Constraint(ruleSet, PutawayConstraints.WeightCapacity.RuleCode, order: 70);

        // Weights sum to 1, so a score reads as a fraction of the ideal rather
        // than as an arbitrary magnitude. Nothing enforces that — it is a
        // convention that makes traces legible.
        Preference(ruleSet, PutawayPreferences.ConsolidateSameItem.RuleCode, order: 100, weight: 0.35m);
        Preference(ruleSet, PutawayPreferences.VelocitySlotting.RuleCode, order: 110, weight: 0.25m);
        Preference(ruleSet, PutawayPreferences.TravelDistance.RuleCode, order: 120, weight: 0.25m);
        Preference(ruleSet, PutawayPreferences.CubeUtilisation.RuleCode, order: 130, weight: 0.15m);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded rule set {Name} v{Version} with {Rules} rules",
            ruleSet.Name,
            ruleSet.Version,
            ruleSet.Rules.Count);
    }

    private static void Constraint(RuleSet ruleSet, string code, int order) =>
        ruleSet.Rules.Add(new RuleDefinition
        {
            RuleSetId = ruleSet.Id,
            Code = code,
            Role = RuleRole.Constraint,
            Order = order,
            Weight = 0m
        });

    private static void Preference(RuleSet ruleSet, string code, int order, decimal weight) =>
        ruleSet.Rules.Add(new RuleDefinition
        {
            RuleSetId = ruleSet.Id,
            Code = code,
            Role = RuleRole.Preference,
            Order = order,
            Weight = weight
        });
}
