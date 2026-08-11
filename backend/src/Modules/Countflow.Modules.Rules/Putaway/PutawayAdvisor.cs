using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// Chooses where a pallet goes, and writes down why.
/// <para>
/// The whole of Phase 3's first pillar in one method: gather the inputs, run the
/// shared engine, persist the trace, hand back an identifier the caller stamps
/// onto the movement it is about to post. Note the order — the trace is committed
/// <b>before</b> the advice is returned, because a movement pointing at a trace
/// that was never written looks like an explanation and is not one.
/// </para>
/// </summary>
public sealed class PutawayAdvisor(
    RulesDbContext dbContext,
    DecisionJournal journal,
    IItemDirectory items,
    ILocationDirectory locations,
    IStockQuery stock,
    ILogger<PutawayAdvisor> logger) : IPutawayAdvisor
{
    public async Task<Result<PutawayAdvice>> SuggestAsync(
        PutawayQuery query,
        CancellationToken cancellationToken) =>
        await EvaluateAsync(query, isPreview: false, cancellationToken);

    /// <summary>
    /// The same evaluation, flagged as a question rather than a decision. Used by
    /// the console's "where would you put this?" — a supervisor rehearsing a
    /// change is exactly the audience ADR 0004 was written for, and giving them a
    /// separate, subtly different code path would defeat the purpose.
    /// </summary>
    public Task<Result<PutawayAdvice>> PreviewAsync(PutawayQuery query, CancellationToken cancellationToken) =>
        EvaluateAsync(query, isPreview: true, cancellationToken);

    private async Task<Result<PutawayAdvice>> EvaluateAsync(
        PutawayQuery query,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (query.Quantity <= 0m)
        {
            return Result.Failure<PutawayAdvice>(RulesErrors.QuantityNotPositive);
        }

        var item = await items.FindAsync(query.ItemId, cancellationToken);
        if (item is null)
        {
            return Result.Failure<PutawayAdvice>(RulesErrors.ItemNotFound);
        }

        var ruleSet = await FindActiveRuleSetAsync(DecisionKind.Putaway, query.WarehouseId, cancellationToken);
        if (ruleSet is null)
        {
            return Result.Failure<PutawayAdvice>(RulesErrors.NoActiveRuleSet(nameof(DecisionKind.Putaway)));
        }

        var built = PutawayRuleCatalogue.Build(ruleSet);
        if (built.IsFailure)
        {
            return Result.Failure<PutawayAdvice>(built.Error);
        }

        var candidates = await locations.FindStorageCandidatesAsync(query.WarehouseId, cancellationToken);
        if (candidates.Count == 0)
        {
            return Result.Failure<PutawayAdvice>(RulesErrors.LocationNotFound);
        }

        var occupancy = await stock.OccupancyAsync(
            [.. candidates.Select(c => c.Id)], query.ItemId, query.LotId, cancellationToken);

        var context = new PutawayContext(
            item,
            query.LotId,
            query.Quantity,
            query.Condition,
            query.FromLocationId,
            occupancy,
            RequiredVolumeM3: item.UnitVolumeM3 * query.Quantity,
            RequiredWeightKg: item.UnitWeightKg * query.Quantity,
            FurthestPickSequence: candidates.Max(c => ((long)c.ZonePickSequence * 1_000_000) + c.PickSequence));

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway,
            built.Value.Stamp,
            candidates,
            context,
            built.Value.Constraints,
            built.Value.Preferences);

        var traceId = await journal.RecordAsync(
            decision,
            new PutawayInputs(
                query.WarehouseId,
                query.ItemId,
                item.Sku,
                query.LotId,
                query.Quantity,
                query.Condition,
                query.FromLocationId,
                query.LicensePlateId),
            query.Actor,
            isPreview,
            cancellationToken);

        var winner = decision.Winner;

        logger.LogInformation(
            "Putaway {Mode} for {Sku} ×{Quantity}: {Outcome} from {Admissible}/{Considered} candidates " +
            "under {RuleSet} v{Version} in {Elapsed}µs (trace {TraceId})",
            isPreview ? "preview" : "decision",
            item.Sku,
            query.Quantity,
            winner?.Candidate.Code ?? "no location",
            decision.AdmissibleCount,
            decision.ConsideredCount,
            ruleSet.Name,
            ruleSet.Version,
            (long)decision.Elapsed.TotalMicroseconds,
            traceId);

        return new PutawayAdvice(
            traceId,
            winner?.Candidate.Id,
            winner?.Candidate.Code,
            winner?.Score ?? 0m,
            winner?.Scores ?? [],
            decision.ConsideredCount,
            decision.AdmissibleCount);
    }

    /// <summary>
    /// The warehouse's own set if it has one, otherwise the shared default.
    /// <para>
    /// A site with genuinely different physics — a cold store, a hazardous depot —
    /// overrides; everyone else inherits, so tuning the common case is one edit
    /// rather than one per warehouse.
    /// </para>
    /// </summary>
    private async Task<RuleSet?> FindActiveRuleSetAsync(
        DecisionKind kind,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        var sets = await dbContext.RuleSets
            .AsNoTracking()
            .Include(r => r.Rules)
            .Where(r => r.Kind == kind
                        && r.Status == RuleSetStatus.Active
                        && (r.WarehouseId == null || r.WarehouseId == warehouseId))
            .ToListAsync(cancellationToken);

        return sets.FirstOrDefault(r => r.WarehouseId == warehouseId) ?? sets.FirstOrDefault();
    }

    /// <summary>
    /// What was asked, stored on the trace so Phase 6 can ask it again of a
    /// candidate rule set. The warehouse <i>state</i> is not stored here — the
    /// ledger already is one, and occupancy as of the trace's timestamp is
    /// replayable exactly (ADR 0001).
    /// </summary>
    private sealed record PutawayInputs(
        Guid WarehouseId,
        Guid ItemId,
        string Sku,
        Guid? LotId,
        decimal Quantity,
        StockCondition Condition,
        Guid FromLocationId,
        Guid? LicensePlateId);
}
