using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Dispositions;

/// <summary>
/// Decides what becomes of a returned unit, and writes down why.
/// <para>
/// The fourth advisor, and structurally the third one over again: gather the
/// inputs, run the shared engine, persist the trace, hand back an identifier the
/// caller stamps onto whatever it posts. The trace is committed <b>before</b> the
/// advice is returned, for the reason it always is — a movement pointing at a
/// trace that was never written looks like an explanation and is not one.
/// </para>
/// <para>
/// What is different is what it is advising <i>about</i>. The candidates are four
/// fixed verbs rather than rows, so there is no candidate query: the field is
/// <see cref="DispositionOption.All"/>, always, in the same order, on every
/// decision. That makes this the cheapest evaluation in the system and the one
/// whose trace is most worth reading, because a four-line trace showing three
/// exclusions and one survivor is the complete argument for having destroyed
/// somebody's stock.
/// </para>
/// </summary>
public sealed class DispositionAdvisor(
    RulesDbContext dbContext,
    DecisionJournal journal,
    IItemDirectory items,
    ILocationDirectory locations,
    TimeProvider clock,
    ILogger<DispositionAdvisor> logger) : IDispositionAdvisor
{
    public Task<Result<DispositionAdvice>> AdviseAsync(
        DispositionQuery query,
        CancellationToken cancellationToken) =>
        EvaluateAsync(query, isPreview: false, cancellationToken);

    /// <summary>
    /// The same evaluation, flagged as a question rather than a decision. Behind
    /// the console's "what would you do with this?", which is how a supervisor
    /// tunes a returns policy without a pallet in front of them.
    /// </summary>
    public Task<Result<DispositionAdvice>> PreviewAsync(
        DispositionQuery query,
        CancellationToken cancellationToken) =>
        EvaluateAsync(query, isPreview: true, cancellationToken);

    private async Task<Result<DispositionAdvice>> EvaluateAsync(
        DispositionQuery query,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (query.Quantity <= 0m)
        {
            return Result.Failure<DispositionAdvice>(RulesErrors.QuantityNotPositive);
        }

        var item = await items.FindAsync(query.ItemId, cancellationToken);
        if (item is null)
        {
            return Result.Failure<DispositionAdvice>(RulesErrors.ItemNotFound);
        }

        var ruleSet = await FindActiveRuleSetAsync(
            DecisionKind.Disposition, query.WarehouseId, cancellationToken);

        if (ruleSet is null)
        {
            return Result.Failure<DispositionAdvice>(
                RulesErrors.NoActiveRuleSet(nameof(DecisionKind.Disposition)));
        }

        var built = DispositionRuleCatalogue.Build(ruleSet);
        if (built.IsFailure)
        {
            return Result.Failure<DispositionAdvice>(built.Error);
        }

        // Whether the warehouse can send anything out for repair at all. Fetched
        // here with everything else rather than inside the rule, because the engine
        // is a pure function and a constraint that issued a query would be a
        // constraint replay could not run.
        var transit = await locations.FindCounterpartyAsync(
            query.WarehouseId, CounterpartyKind.InTransit, cancellationToken);

        var context = new DispositionContext(
            item,
            query.LotId,
            query.Quantity,
            query.AtLocationId,
            query.Findings,
            RepairRouteExists: transit is { IsActive: true },
            query.DaysSinceDespatch,
            ShelfLifeRemaining: RemainingShelfLife(
                item, query.ExpiryDate, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)));

        var decision = RuleEngine.Evaluate(
            DecisionKind.Disposition,
            built.Value.Stamp,
            DispositionOption.All,
            context,
            built.Value.Constraints,
            built.Value.Preferences);

        var traceId = await journal.RecordAsync(
            decision,
            new DispositionInputs(
                query.WarehouseId,
                query.ItemId,
                item.Sku,
                query.LotId,
                query.Quantity,
                query.AtLocationId,
                query.Findings.Condition,
                query.Findings.IsSealed,
                query.Findings.IsRepairable,
                query.DaysSinceDespatch,
                query.ExpiryDate,
                context.RepairRouteExists,
                clock.GetUtcNow()),
            query.Actor,
            isPreview,
            cancellationToken);

        var winner = decision.Winner;

        logger.LogInformation(
            "Disposition {Mode} for {Sku} ×{Quantity}: {Outcome} from {Admissible}/{Considered} options " +
            "under {RuleSet} v{Version} in {Elapsed}µs (trace {TraceId})",
            isPreview ? "preview" : "decision",
            item.Sku,
            query.Quantity,
            winner?.Candidate.Key ?? "no disposition",
            decision.AdmissibleCount,
            decision.ConsideredCount,
            ruleSet.Name,
            ruleSet.Version,
            (long)decision.Elapsed.TotalMicroseconds,
            traceId);

        return new DispositionAdvice(
            traceId,
            winner?.Candidate.Disposition,
            winner?.Score ?? 0m,
            winner?.Scores ?? [],
            decision.ConsideredCount,
            decision.AdmissibleCount);
    }

    /// <summary>
    /// What is left of the batch's life, as a fraction of the item's own shelf
    /// life.
    /// <para>
    /// Null when the item has no shelf life on file or the batch has no expiry —
    /// "cannot tell", which the rules treat as no objection rather than as none
    /// left. A batch already past its date returns zero rather than a negative, so
    /// the value stays on <c>[0, 1]</c> and the engine's clamp never has to do it.
    /// </para>
    /// </summary>
    internal static decimal? RemainingShelfLife(ItemSnapshot item, DateOnly? expiryDate, DateOnly today)
    {
        if (expiryDate is not { } expiry || item.ShelfLifeDays is not { } life || life <= 0)
        {
            return null;
        }

        var daysLeft = expiry.DayNumber - today.DayNumber;

        return Math.Clamp((decimal)daysLeft / life, 0m, 1m);
    }

    /// <summary>The warehouse's own set if it has one, otherwise the shared default.</summary>
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
}
