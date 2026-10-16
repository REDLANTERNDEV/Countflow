using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>Evaluates the caller's frozen contents and commits the explanation before returning.</summary>
public sealed class PalletPutawayAdvisor(
    RulesDbContext dbContext,
    DecisionJournal journal,
    IItemDirectory items,
    ILocationDirectory locations,
    IStockQuery stock,
    ILogger<PalletPutawayAdvisor> logger) : IPalletPutawayAdvisor
{
    public async Task<Result<PutawayAdvice>> SuggestAsync(PalletPutawayQuery query, CancellationToken cancellationToken)
    {
        // Own a copy before the first await: the caller may later receive more
        // stock, but this evaluation and its stored intent must stay identical.
        var contents = query.Contents.OrderBy(line => line.ItemId).ThenBy(line => line.LotId)
            .ThenBy(line => line.Condition).ToArray();
        if (contents.Length == 0 || contents.Any(line => line.Quantity <= 0m))
            return Result.Failure<PutawayAdvice>(RulesErrors.QuantityNotPositive);

        var sets = await dbContext.RuleSets.AsNoTracking().Include(set => set.Rules)
            .Where(set => set.Kind == DecisionKind.Putaway && set.Status == RuleSetStatus.Active
                && (set.WarehouseId == query.WarehouseId || set.WarehouseId == null))
            .ToListAsync(cancellationToken);
        var ruleSet = sets.FirstOrDefault(set => set.WarehouseId == query.WarehouseId) ?? sets.FirstOrDefault();
        if (ruleSet is null)
            return Result.Failure<PutawayAdvice>(RulesErrors.NoActiveRuleSet(nameof(DecisionKind.Putaway)));
        var rules = PalletPutawayRuleCatalogue.Build(ruleSet);
        if (rules.IsFailure) return Result.Failure<PutawayAdvice>(rules.Error);

        var candidates = await locations.FindStorageCandidatesAsync(query.WarehouseId, cancellationToken);
        if (candidates.Count == 0) return Result.Failure<PutawayAdvice>(RulesErrors.LocationNotFound);
        var itemsById = await items.FindManyAsync([.. contents.Select(line => line.ItemId).Distinct()], cancellationToken);
        if (contents.Any(line => !itemsById.ContainsKey(line.ItemId)))
            return Result.Failure<PutawayAdvice>(RulesErrors.ItemNotFound);
        var occupancy = await stock.OccupancyAsync([.. candidates.Select(candidate => candidate.Id)],
            [.. contents.Select(line => new StockSubject(line.ItemId, line.LotId)).Distinct()], cancellationToken);
        var context = PalletPutawayContext.Create(contents, query.FromLocationId, itemsById,
            occupancy, RouteScale.Of(candidates));
        if (context.IsFailure) return Result.Failure<PutawayAdvice>(context.Error);

        var decision = RuleEngine.Evaluate(DecisionKind.Putaway, rules.Value.Stamp, candidates,
            context.Value, rules.Value.Constraints, rules.Value.Preferences);
        var first = contents[0];
        // Legacy fields keep their original single-line meanings. The optional
        // PalletContents field alone selects whole-load replay semantics.
        var traceId = await journal.RecordAsync(decision,
            new PutawayInputs(query.WarehouseId, first.ItemId, itemsById[first.ItemId].Sku,
                first.LotId, first.Quantity, first.Condition, query.FromLocationId,
                query.LicensePlateId, contents), query.Actor, isPreview: false, cancellationToken);
        var winner = decision.Winner;
        logger.LogInformation("Pallet putaway for {PlateId}: {Outcome} from {Admissible}/{Considered} candidates (trace {TraceId})",
            query.LicensePlateId, winner?.Candidate.Code ?? "no location",
            decision.AdmissibleCount, decision.ConsideredCount, traceId);
        return new PutawayAdvice(traceId, winner?.Candidate.Id, winner?.Candidate.Code,
            winner?.Score ?? 0m, winner?.Scores ?? [], decision.ConsideredCount, decision.AdmissibleCount);
    }
}
