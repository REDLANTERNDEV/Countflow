using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Counting.Features.Plans;

/// <summary>
/// Turns stored plans into something a supervisor can read, and closes the ones
/// whose work is done.
/// <para>
/// The closing is done on read rather than by anything watching the queue, and
/// that is the convergent argument once more: this module raises jobs and has no
/// way to be told they finished — Work has never heard of it, and a callback would
/// be the coupling ADR 0002 forbids. So the plan's status is derived from what the
/// queue says whenever somebody looks, and persisted when it changes so the
/// already-scheduled exclusion stops holding those holdings back.
/// </para>
/// <para>
/// The counted quantities come from the queue too, for the same reason. What each
/// count <i>found</i> is authoritative in the <c>inventory</c> schema, where the
/// variance was computed against the book as it stood at that instant; what is
/// shown here is the operator's reported figure against the plan's own expectation,
/// which is the useful thing on this screen and is labelled as indicative.
/// </para>
/// </summary>
public sealed class CountPlanDescriber(
    CountingDbContext dbContext,
    IWorkQueue queue,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IStockQuery stock,
    TimeProvider clock)
{
    public async Task<CountPlanResponse> DescribeAsync(CountPlan plan, CancellationToken cancellationToken)
    {
        var states = await ReadStatesAsync([plan], cancellationToken);

        await CloseIfFinishedAsync([plan], states, cancellationToken);

        var itemsById = await items.FindManyAsync(
            [.. plan.Lines.Select(l => l.ItemId).Distinct()], cancellationToken);

        var lotsById = await stock.FindLotsAsync(
            [.. plan.Lines.Where(l => l.LotId is not null).Select(l => l.LotId!.Value).Distinct()],
            cancellationToken);

        var warehouse = await warehouses.FindAsync(plan.WarehouseId, cancellationToken);

        var lines = plan.Lines
            .OrderBy(line => line.Rank)
            .Select(line =>
            {
                var summary = line.WorkTaskId is { } taskId ? states.GetValueOrDefault(taskId) : null;

                return new CountPlanLineResponse(
                    line.Id,
                    itemsById.GetValueOrDefault(line.ItemId)?.Sku ?? string.Empty,
                    line.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId)?.LotNumber : null,
                    line.LocationCode,
                    line.Condition,
                    line.ExpectedQuantity,
                    line.Rank,
                    line.Score,
                    line.WorkTaskId,
                    summary?.State,
                    summary?.CountedQuantity,
                    summary?.CountedQuantity is { } counted ? counted - line.ExpectedQuantity : null,
                    summary?.CompletedAt,
                    summary?.CompletedByUserName);
            })
            .ToList();

        return new CountPlanResponse(
            plan.Id,
            plan.Reference,
            plan.Status,
            warehouse?.Code ?? string.Empty,
            plan.RequestedTasks,
            lines.Count(line => line.WorkTaskId is not null),
            lines.Count(line => line.State == WorkTaskState.Completed),
            lines.Count(line => line.Variance is not null and not 0m),
            plan.DecisionTraceId,
            plan.CreatedAt,
            plan.CreatedByUserName,
            plan.CompletedAt,
            plan.Notes,
            lines);
    }

    public async Task<IReadOnlyList<CountPlanSummaryResponse>> SummariseAsync(
        IReadOnlyList<CountPlan> plans,
        CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return [];
        }

        var states = await ReadStatesAsync(plans, cancellationToken);

        await CloseIfFinishedAsync(plans, states, cancellationToken);

        return
        [
            .. plans.Select(plan =>
            {
                var summaries = plan.Lines
                    .Select(line => line.WorkTaskId is { } taskId ? states.GetValueOrDefault(taskId) : null)
                    .ToList();

                return new CountPlanSummaryResponse(
                    plan.Id,
                    plan.Reference,
                    plan.Status,
                    plan.RequestedTasks,
                    plan.Lines.Count(line => line.WorkTaskId is not null),
                    summaries.Count(s => s?.State == WorkTaskState.Completed),
                    plan.Lines.Count(line =>
                    {
                        var summary = line.WorkTaskId is { } id ? states.GetValueOrDefault(id) : null;
                        return summary?.CountedQuantity is { } counted && counted != line.ExpectedQuantity;
                    }),
                    plan.DecisionTraceId,
                    plan.CreatedAt,
                    plan.CreatedByUserName);
            })
        ];
    }

    private async Task<IReadOnlyDictionary<Guid, WorkTaskSummary>> ReadStatesAsync(
        IReadOnlyList<CountPlan> plans,
        CancellationToken cancellationToken) =>
        await queue.FindManyAsync(
            [
                .. plans
                    .SelectMany(plan => plan.Lines)
                    .Where(line => line.WorkTaskId is not null)
                    .Select(line => line.WorkTaskId!.Value)
            ],
            cancellationToken);

    /// <summary>
    /// Marks a plan finished once every line it raised has been confirmed or
    /// withdrawn.
    /// <para>
    /// A line with no job is deliberately <i>not</i> finished. It is a count this
    /// module still owes, and closing the plan around it would drop it from the
    /// already-scheduled set and let the policy re-propose the bay — which is the
    /// same holding, on a fresh plan, with nobody ever having counted it.
    /// </para>
    /// </summary>
    private async Task CloseIfFinishedAsync(
        IReadOnlyList<CountPlan> plans,
        IReadOnlyDictionary<Guid, WorkTaskSummary> states,
        CancellationToken cancellationToken)
    {
        var closed = false;

        foreach (var plan in plans.Where(p => p.Status == CountPlanStatus.Open && p.Lines.Count > 0))
        {
            var finished = plan.Lines.All(line =>
                line.WorkTaskId is { } taskId
                && states.GetValueOrDefault(taskId) is
                    { State: WorkTaskState.Completed or WorkTaskState.Cancelled });

            if (!finished)
            {
                continue;
            }

            plan.Status = CountPlanStatus.Completed;
            plan.CompletedAt = clock.GetUtcNow();
            closed = true;
        }

        if (closed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
