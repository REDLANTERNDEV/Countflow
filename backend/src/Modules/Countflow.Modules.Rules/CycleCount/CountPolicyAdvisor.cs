using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.CycleCount;

/// <summary>
/// Chooses what the warehouse should count next, and writes down why.
/// <para>
/// The same five steps as the other two advisors — gather, evaluate, take, record,
/// hand back an identifier — and the simplest of the three, which is the point
/// worth noticing. Putaway needs a winner; allocation needs a fill that walks the
/// ranking against a demand; counting needs the top <c>N</c> and nothing else.
/// Every scrap of the judgement is in the ranking, and the ranking is traced.
/// </para>
/// <para>
/// Note what this does not do: it does not raise the tasks. It says which holdings
/// are due and hands back the trace that justifies the list, and the module that
/// asked is the one that turns that into work — the same division Rules keeps with
/// putaway and allocation, and the reason this module has never needed to know that
/// a work queue exists.
/// </para>
/// </summary>
public sealed class CountPolicyAdvisor(
    RulesDbContext dbContext,
    DecisionJournal journal,
    IItemDirectory items,
    ILocationDirectory locations,
    IStockCounts counts,
    TimeProvider clock,
    ILogger<CountPolicyAdvisor> logger) : ICountPolicy
{
    public async Task<Result<CountPlanAdvice>> AdviseAsync(
        CountPolicyQuery query,
        CancellationToken cancellationToken) =>
        await EvaluateAsync(query, isPreview: false, cancellationToken);

    /// <summary>
    /// The same evaluation with nothing raised. A supervisor asking "what would
    /// tonight's counts be?" runs the identical advisor rather than a lookalike that
    /// could quietly disagree with it.
    /// </summary>
    public async Task<Result<CountPlanAdvice>> PreviewAsync(
        CountPolicyQuery query,
        CancellationToken cancellationToken) =>
        await EvaluateAsync(query, isPreview: true, cancellationToken);

    private async Task<Result<CountPlanAdvice>> EvaluateAsync(
        CountPolicyQuery query,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (query.MaxTasks <= 0)
        {
            return Result.Failure<CountPlanAdvice>(RulesErrors.QuantityNotPositive);
        }

        var ruleSet = await FindActiveRuleSetAsync(DecisionKind.CycleCount, query.WarehouseId, cancellationToken);
        if (ruleSet is null)
        {
            return Result.Failure<CountPlanAdvice>(
                RulesErrors.NoActiveRuleSet(nameof(DecisionKind.CycleCount)));
        }

        var built = CountRuleCatalogue.Build(ruleSet);
        if (built.IsFailure)
        {
            return Result.Failure<CountPlanAdvice>(built.Error);
        }

        var candidates = await counts.CandidatesAsync(query.WarehouseId, cancellationToken);

        var context = await BuildContextAsync(query, candidates, cancellationToken);

        var decision = RuleEngine.Evaluate(
            DecisionKind.CycleCount,
            built.Value.Stamp,
            candidates,
            context,
            built.Value.Constraints,
            built.Value.Preferences);

        var lines = Take(decision, query.MaxTasks, context);

        var traceId = await journal.RecordAsync(
            decision,
            new CountPolicyInputs(
                query.WarehouseId,
                query.MaxTasks,
                query.AlreadyScheduledBalanceIds.Count,
                context.Now),
            query.Actor,
            isPreview,
            cancellationToken);

        logger.LogInformation(
            "Count policy {Mode} for warehouse {WarehouseId}: {Raised} of {Admissible} due holdings taken from " +
            "{Considered} considered under {RuleSet} v{Version} in {Elapsed}µs (trace {TraceId})",
            isPreview ? "preview" : "decision",
            query.WarehouseId,
            lines.Count,
            decision.AdmissibleCount,
            decision.ConsideredCount,
            ruleSet.Name,
            ruleSet.Version,
            (long)decision.Elapsed.TotalMicroseconds,
            traceId);

        return new CountPlanAdvice(
            traceId,
            lines,
            decision.ConsideredCount,
            decision.AdmissibleCount);
    }

    /// <summary>
    /// Takes the top of the ranking, and nothing cleverer.
    /// <para>
    /// Allocation's fill has to walk the ranking because a demand can outrun a
    /// holding; this has no such complication and the temptation is therefore to add
    /// one — spread the shift's counts across zones, say, or balance the bands.
    /// Every one of those would be judgement the trace cannot explain, and the first
    /// time somebody asked why the second-ranked bay was skipped there would be no
    /// answer. If a warehouse wants its counts spread across zones, that belongs in
    /// the rule set as a preference where it can be seen, weighed and switched off.
    /// </para>
    /// </summary>
    private static List<CountDueLine> Take(
        Decision<CountCandidate> decision,
        int maxTasks,
        CountContext context)
    {
        return
        [
            .. decision.Candidates
                .Where(evaluation => evaluation.IsAdmissible)
                .Take(maxTasks)
                .Select(evaluation =>
                {
                    var candidate = evaluation.Candidate;

                    return new CountDueLine(
                        candidate.Id,
                        candidate.ItemId,
                        candidate.LotId,
                        candidate.LotNumber,
                        candidate.LocationId,
                        context.LocationOf(candidate)?.Code ?? candidate.LocationCode,
                        candidate.LicensePlateId,
                        candidate.Condition,
                        candidate.OnHandQuantity,
                        evaluation.Rank,
                        evaluation.Score);
                })
        ];
    }

    private async Task<CountContext> BuildContextAsync(
        CountPolicyQuery query,
        IReadOnlyList<CountCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var itemsById = await items.FindManyAsync(
            [.. candidates.Select(c => c.ItemId).Distinct()], cancellationToken);

        // The whole warehouse rather than just the candidates' locations, which is
        // putaway's query and not allocation's. Travel has to be normalised against
        // the building (see CountContext.NormalisedDistance), and the same fetch
        // supplies both the scale and the snapshots the rules read — so it is one
        // query rather than two, on a decision that runs once a shift.
        var storage = await locations.FindStorageCandidatesAsync(query.WarehouseId, cancellationToken);

        var locationsById = storage.ToDictionary(location => location.Id);

        // Everything the warehouse holds of each item, across every candidate
        // including the ones the constraints will refuse. That is the right
        // denominator: the exposure a wrong number represents is a fraction of the
        // item's whole position, and quarantined stock is part of the position.
        var onHandByItem = candidates
            .GroupBy(c => c.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(c => c.OnHandQuantity));

        // The route window across the candidates, which the shipped rule set does
        // not use and TravelScale.Field does. Computed from what has already been
        // fetched rather than from a second query, so carrying it costs nothing.
        var field = candidates
            .Select(candidate => locationsById.GetValueOrDefault(candidate.LocationId))
            .Where(location => location is not null)
            .Select(location => location!);

        return new CountContext(
            clock.GetUtcNow(),
            itemsById,
            locationsById,
            query.AlreadyScheduledBalanceIds.ToHashSet(),
            onHandByItem,
            RouteScale.Of(storage),
            RouteScale.Of(field));
    }

    /// <summary>
    /// The warehouse's own set if it has one, otherwise the shared default —
    /// resolved exactly as the other two advisors resolve theirs. Three copies of
    /// six lines now, and still the right trade: a generic lookup would have to be
    /// parameterised by the candidate and context types the three advisors
    /// deliberately do not share.
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

    // The shape moved to Replay/DecisionInputs.cs, where the reason it is the
    // thinnest of the three is set out: almost every input to a counting decision is
    // derivable from two append-only histories, so replaying last month's policy
    // needs little more than the clock.
}
