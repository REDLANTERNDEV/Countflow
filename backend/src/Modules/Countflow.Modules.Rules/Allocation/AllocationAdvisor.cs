using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Allocation;

/// <summary>
/// Chooses which stock satisfies an order line, and writes down why.
/// <para>
/// The same five steps as <c>PutawayAdvisor</c> — gather, evaluate, record, hand
/// back an identifier — with one structural difference worth being explicit
/// about: <b>one decision produces several lines</b>. Putaway asks "which bin?"
/// and there is exactly one answer; allocation asks "which stock?" and a hundred
/// units may be forty here and sixty there.
/// </para>
/// <para>
/// The engine still returns a single ranking, and that is the point. The rules do
/// the judging; the fill is a deterministic walk down the ranking taking what each
/// holding has until the demand is met. Anything cleverer — a solver that
/// minimised the number of holdings, say — would be judgement the trace could not
/// explain, which is precisely the thing ADR 0004 exists to prevent.
/// </para>
/// </summary>
public sealed class AllocationAdvisor(
    RulesDbContext dbContext,
    DecisionJournal journal,
    IItemDirectory items,
    ILocationDirectory locations,
    IStockAvailability availability,
    TimeProvider clock,
    ILogger<AllocationAdvisor> logger) : IAllocationAdvisor
{
    public Task<Result<AllocationPlan>> AllocateAsync(
        AllocationQuery query,
        CancellationToken cancellationToken) =>
        EvaluateAsync(query, isPreview: false, cancellationToken);

    /// <summary>
    /// The same evaluation with nothing reserved. As with putaway, a supervisor
    /// asking "what would this order take?" runs the identical advisor rather than
    /// a lookalike that could quietly disagree with it.
    /// </summary>
    public Task<Result<AllocationPlan>> PreviewAsync(
        AllocationQuery query,
        CancellationToken cancellationToken) =>
        EvaluateAsync(query, isPreview: true, cancellationToken);

    private async Task<Result<AllocationPlan>> EvaluateAsync(
        AllocationQuery query,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (query.Quantity <= 0m)
        {
            return Result.Failure<AllocationPlan>(RulesErrors.QuantityNotPositive);
        }

        var item = await items.FindAsync(query.ItemId, cancellationToken);
        if (item is null)
        {
            return Result.Failure<AllocationPlan>(RulesErrors.ItemNotFound);
        }

        var ruleSet = await FindActiveRuleSetAsync(DecisionKind.Allocation, query.WarehouseId, cancellationToken);
        if (ruleSet is null)
        {
            return Result.Failure<AllocationPlan>(RulesErrors.NoActiveRuleSet(nameof(DecisionKind.Allocation)));
        }

        var built = AllocationRuleCatalogue.Build(ruleSet);
        if (built.IsFailure)
        {
            return Result.Failure<AllocationPlan>(built.Error);
        }

        var candidates = await availability.HoldingsAsync(query.WarehouseId, query.ItemId, cancellationToken);

        var context = await BuildContextAsync(query, item, candidates, cancellationToken);

        var decision = RuleEngine.Evaluate(
            DecisionKind.Allocation,
            built.Value.Stamp,
            candidates,
            context,
            AllocationRuleCatalogue.RequireSourceRole(built.Value, query.RequiredSourceRole).Constraints,
            built.Value.Preferences);

        var lines = Fill(decision, query.Quantity, context);

        var traceId = await journal.RecordAsync(
            decision,
            new AllocationInputs(
                query.WarehouseId,
                query.ItemId,
                item.Sku,
                query.RequiredLotId,
                query.Quantity,
                context.ShipOn, query.RequiredSourceRole),
            query.Actor,
            isPreview,
            cancellationToken);

        var allocated = lines.Sum(line => line.Quantity);

        logger.LogInformation(
            "Allocation {Mode} for {Sku} ×{Quantity}: {Allocated} from {Lines} holdings, {Shortfall} short, " +
            "{Admissible}/{Considered} admissible under {RuleSet} v{Version} in {Elapsed}µs (trace {TraceId})",
            isPreview ? "preview" : "decision",
            item.Sku,
            query.Quantity,
            allocated,
            lines.Count,
            query.Quantity - allocated,
            decision.AdmissibleCount,
            decision.ConsideredCount,
            ruleSet.Name,
            ruleSet.Version,
            (long)decision.Elapsed.TotalMicroseconds,
            traceId);

        return new AllocationPlan(
            traceId,
            lines,
            allocated,
            query.Quantity - allocated,
            decision.ConsideredCount,
            decision.AdmissibleCount);
    }

    /// <summary>
    /// Walks the ranking taking what each holding can give until the demand is
    /// met.
    /// <para>
    /// Deliberately greedy and deliberately dumb. The intelligence is in the
    /// ranking, which is traced; adding intelligence here would put judgement
    /// somewhere the trace does not reach, and the first time somebody asked why
    /// an order took the second-best batch there would be no answer.
    /// </para>
    /// <para>
    /// Uses the <i>available</i> quantity, never the on-hand one. Taking a
    /// holding's full contents when half of it is promised elsewhere is how one
    /// order quietly steals another's stock, and it would not surface until the
    /// second picker found an empty bin.
    /// </para>
    /// </summary>
    private static List<AllocationLine> Fill(
        Decision<StockHolding> decision,
        decimal demand,
        AllocationContext context)
    {
        var lines = new List<AllocationLine>();
        var outstanding = demand;

        foreach (var evaluation in decision.Candidates.Where(c => c.IsAdmissible))
        {
            if (outstanding <= 0m)
            {
                break;
            }

            var holding = evaluation.Candidate;
            var take = Math.Min(outstanding, holding.AvailableQuantity);

            if (take <= 0m)
            {
                continue;
            }

            lines.Add(new AllocationLine(
                holding.Id,
                holding.ItemId,
                holding.LotId,
                holding.LotNumber,
                holding.ExpiryDate,
                holding.LocationId,
                context.LocationOf(holding)?.Code ?? holding.LocationCode,
                holding.LicensePlateId,
                take,
                evaluation.Score));

            outstanding -= take;
        }

        return lines;
    }

    private async Task<AllocationContext> BuildContextAsync(
        AllocationQuery query,
        ItemSnapshot item,
        IReadOnlyList<StockHolding> candidates,
        CancellationToken cancellationToken)
    {
        var locationsById = await locations.FindStorageLocationsAsync(
            [.. candidates.Select(c => c.LocationId).Distinct()], cancellationToken);

        var expiries = candidates
            .Where(c => c.ExpiryDate is not null)
            .Select(c => c.ExpiryDate!.Value)
            .ToList();
        var receipts = candidates.Where(candidate => candidate.FirstReceivedAt is not null).Select(candidate => candidate.FirstReceivedAt!.Value).ToList();

        // The route window across the candidates, not across the warehouse. See
        // AllocationContext.NormalisedDistance: a line satisfiable from four bins
        // in one aisle should still rank those four against each other, which
        // measuring them against a twenty-four-aisle hall would not do.
        var positions = locationsById.Values
            .Select(l => ((long)l.ZonePickSequence * 1_000_000) + l.PickSequence)
            .ToList();

        return new AllocationContext(
            item,
            query.Quantity,
            query.RequiredLotId,
            query.ShipBy ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
            locationsById,
            expiries.Count == 0 ? null : expiries.Min(),
            expiries.Count == 0 ? null : expiries.Max(),
            positions.Count == 0 ? 0L : positions.Min(),
            positions.Count == 0 ? 0L : positions.Max())
        {
            EarliestReceipt = receipts.Count == 0 ? null : receipts.Min(),
            LatestReceipt = receipts.Count == 0 ? null : receipts.Max()
        };
    }

    /// <summary>
    /// The warehouse's own set if it has one, otherwise the shared default —
    /// resolved exactly as putaway's is. Two copies of six lines, and the
    /// alternative was a generic lookup that would have had to be parameterised by
    /// the very type the two advisors do not share.
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

    // The shape moved to Replay/DecisionInputs.cs. Phase 4 recorded here that
    // reservations were the one input a replay could not reconstruct; Phase 6 found
    // that a claim carries CreatedAt and a set-once ClosedAt and is therefore
    // recoverable after all — see WarehouseAsOf.ReservedByBalance.
}
