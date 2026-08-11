using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.CycleCount;
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
        // Checked per kind rather than "are there any rule sets", because Phase 4
        // adds a second kind to a database that already has the first. An
        // upgrade seeds only what is missing.
        await SeedPutawayAsync(cancellationToken);
        await SeedAllocationAsync(cancellationToken);
        await SeedCycleCountAsync(cancellationToken);
    }

    private async Task SeedPutawayAsync(CancellationToken cancellationToken)
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

    private async Task SeedAllocationAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.RuleSets.AnyAsync(r => r.Kind == DecisionKind.Allocation, cancellationToken))
        {
            logger.LogInformation("Allocation rule set already present; skipping seed");
            return;
        }

        var ruleSet = new RuleSet
        {
            Kind = DecisionKind.Allocation,
            Name = "Standard allocation",
            Version = 1,
            Status = RuleSetStatus.Active,
            WarehouseId = null,
            ActivatedAt = DateTimeOffset.UtcNow,
            Notes = "Shipped default. FEFO first, and short-dated stock is refused rather than ranked down."
        };

        dbContext.RuleSets.Add(ruleSet);

        // Availability first because it is the cheapest and the most common
        // exclusion once a warehouse is busy; condition and location before the
        // dates because "that pallet is in quarantine" is a more useful sentence
        // than "that pallet is short-dated" when both are true.
        Constraint(ruleSet, AllocationConstraints.StockAvailable.RuleCode, order: 10);
        Constraint(ruleSet, AllocationConstraints.ConditionAllocatable.RuleCode, order: 20);
        Constraint(ruleSet, AllocationConstraints.PickableLocation.RuleCode, order: 30);
        Constraint(ruleSet, AllocationConstraints.LotMatch.RuleCode, order: 40);
        Constraint(ruleSet, AllocationConstraints.NotExpired.RuleCode, order: 50);
        Constraint(ruleSet, AllocationConstraints.MinimumRemainingShelfLife.RuleCode, order: 60);

        // Rotation carries the most weight by a distance: it is the rule the whole
        // business of tracking expiry exists to serve, and a warehouse that lets
        // travel outvote it is one that writes off stock at the back of the rack.
        Preference(ruleSet, AllocationPreferences.FirstExpiredFirstOut.RuleCode, order: 100, weight: 0.45m);
        Preference(ruleSet, AllocationPreferences.PreferPickFace.RuleCode, order: 110, weight: 0.25m);
        Preference(ruleSet, AllocationPreferences.ClearTheHolding.RuleCode, order: 120, weight: 0.20m);
        Preference(ruleSet, AllocationPreferences.TravelDistance.RuleCode, order: 130, weight: 0.10m);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded rule set {Name} v{Version} with {Rules} rules",
            ruleSet.Name,
            ruleSet.Version,
            ruleSet.Rules.Count);
    }

    private async Task SeedCycleCountAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.RuleSets.AnyAsync(r => r.Kind == DecisionKind.CycleCount, cancellationToken))
        {
            logger.LogInformation("Cycle count rule set already present; skipping seed");
            return;
        }

        var ruleSet = new RuleSet
        {
            Kind = DecisionKind.CycleCount,
            Name = "Standard cycle count",
            Version = 1,
            Status = RuleSetStatus.Active,
            WarehouseId = null,
            ActivatedAt = DateTimeOffset.UtcNow,
            Notes = "Shipped default. ABC-paced, and it will not count a bay that is being worked."
        };

        dbContext.RuleSets.Add(ruleSet);

        // The location first because it is the cheapest and the most complete
        // answer — "that is a pack bench" ends the conversation, where "it was
        // counted on Tuesday" invites the next question. The quiet period before the
        // scheduling check for the same reason: a bay mid-putaway is not a bay
        // somebody should be asked about at all, whether or not it is already on a
        // list.
        Constraint(ruleSet, CountConstraints.CountableLocation.RuleCode, order: 10);
        Constraint(ruleSet, CountConstraints.SettledStock.RuleCode, order: 20);
        Constraint(ruleSet, CountConstraints.NotAlreadyScheduled.RuleCode, order: 30);
        Constraint(ruleSet, CountConstraints.NotRecentlyCounted.RuleCode, order: 40);

        // Staleness carries the most by a distance, for the reason FEFO does in
        // allocation: the other three are tie-breaks on top of "nobody has checked
        // this in a very long time", and a policy that let travel outvote it would
        // count the front of the building forever.
        Preference(ruleSet, CountPreferences.Overdue.RuleCode, order: 100, weight: 0.40m);
        Preference(ruleSet, CountPreferences.Velocity.RuleCode, order: 110, weight: 0.25m);
        Preference(ruleSet, CountPreferences.StockAtRisk.RuleCode, order: 120, weight: 0.20m);
        Preference(ruleSet, CountPreferences.TravelDistance.RuleCode, order: 130, weight: 0.15m);

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
