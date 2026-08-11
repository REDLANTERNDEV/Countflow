using System.Globalization;
using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Countflow.Modules.Counting.Features.Plans;

/// <summary>
/// Turns "count something tonight" into jobs on operators' handhelds.
/// <para>
/// <b>Convergent, not transactional</b>, exactly as an order release is and for the
/// same unavoidable reason: the trace is written in the <c>rules</c> schema, the
/// plan in this one and the jobs in <c>work</c>, so no transaction spans them
/// (ADR 0002). The order the three writes happen in is therefore a choice, and it
/// is made so that every partial state is one a later run can finish.
/// </para>
/// <list type="number">
/// <item><b>Decide</b>, which commits a trace. A trace with no plan behind it is an
/// evaluation somebody ran and did not act on, which is exactly what a preview is
/// and is harmless.</item>
/// <item><b>Record</b> the plan and its lines, with no jobs raised. A line with no
/// task is a count this module intended and has not managed to raise, and the next
/// run of the same request picks it up.</item>
/// <item><b>Raise</b> the jobs, stamping each task id onto its line as it lands.
/// Every enqueue carries the line's own derived key, so a job raised twice is
/// recognised by the queue and merged rather than duplicated.</item>
/// </list>
/// <para>
/// The other order — jobs first, then the plan — would leave counts nothing knows
/// it asked for: not on any plan, and therefore invisible to the policy's
/// already-scheduled exclusion, so the next planning run would propose the same
/// bays again to the same queue.
/// </para>
/// </summary>
public sealed class CountPlanning(
    CountingDbContext dbContext,
    ICountPolicy policy,
    IWorkQueue queue,
    TimeProvider clock,
    ILogger<CountPlanning> logger)
{
    /// <summary>
    /// The most jobs one plan may raise. A ceiling rather than a tuning knob: the
    /// point of cycle counting is that it is continuous and small, and a plan that
    /// raised a thousand counts would be a stocktake wearing the wrong name.
    /// </summary>
    public const int MaxTasksPerPlan = 200;

    public async Task<Result<CountPlan>> PlanAsync(
        Guid warehouseId,
        int maxTasks,
        string? notes,
        string? idempotencyKey,
        PostingActor actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<CountPlan>(CountingErrors.IdempotencyKeyRequired);
        }

        if (maxTasks <= 0)
        {
            return Result.Failure<CountPlan>(CountingErrors.TaskCountNotPositive);
        }

        if (maxTasks > MaxTasksPerPlan)
        {
            return Result.Failure<CountPlan>(CountingErrors.TooManyTasks(MaxTasksPerPlan));
        }

        // A retry converges on the plan it already made rather than asking the
        // policy again. Running the policy twice would not merely waste an
        // evaluation: the second run sees the first run's jobs as already
        // scheduled, so it would return a different set of bays and the retry would
        // quietly become a second plan.
        var existing = await FindByKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return await RaiseOutstandingAsync(existing, cancellationToken);
        }

        var scheduled = await OpenScheduledBalancesAsync(warehouseId, cancellationToken);

        var advice = await policy.AdviseAsync(
            new CountPolicyQuery
            {
                WarehouseId = warehouseId,
                MaxTasks = maxTasks,
                AlreadyScheduledBalanceIds = scheduled,
                Actor = actor
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<CountPlan>(advice.Error);

        var plan = new CountPlan
        {
            WarehouseId = warehouseId,
            Reference = await NextReferenceAsync(warehouseId, cancellationToken),
            RequestedTasks = maxTasks,
            DecisionTraceId = advice.Value.DecisionTraceId,
            IdempotencyKey = idempotencyKey,
            CreatedByUserId = actor.UserId,
            CreatedByUserName = actor.UserName,
            CreatedAt = clock.GetUtcNow(),
            Notes = notes
        };

        foreach (var (line, index) in advice.Value.Lines.Select((line, index) => (line, index)))
        {
            plan.Lines.Add(new CountPlanLine
            {
                CountPlanId = plan.Id,
                BalanceId = line.BalanceId,
                ItemId = line.ItemId,
                LotId = line.LotId,
                LocationId = line.LocationId,
                LocationCode = line.LocationCode,
                LicensePlateId = line.LicensePlateId,
                Condition = line.Condition,
                ExpectedQuantity = line.ExpectedQuantity,
                Rank = line.Rank,
                Score = line.Score,
                IdempotencyKey = $"{idempotencyKey}:{index:D3}"
            });
        }

        dbContext.CountPlans.Add(plan);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Two copies of one retried request arrived close enough together that
            // both got past the check above, or two supervisors pressed the button
            // in the same second and collided on the reference. Either way a unique
            // index settled it; the loser converges on the winner's plan.
            dbContext.ChangeTracker.Clear();

            var winner = await FindByKeyAsync(idempotencyKey, cancellationToken);

            return winner is null
                ? Result.Failure<CountPlan>(CountingErrors.PlanNotFound)
                : await RaiseOutstandingAsync(winner, cancellationToken);
        }

        logger.LogInformation(
            "Count plan {Reference} recorded {Lines} of {Requested} requested holdings " +
            "({Admissible} were due out of {Considered}, trace {TraceId})",
            plan.Reference,
            plan.Lines.Count,
            maxTasks,
            advice.Value.AdmissibleCount,
            advice.Value.ConsideredCount,
            advice.Value.DecisionTraceId);

        return await RaiseOutstandingAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Raises a job for every line that has not got one, and stamps the task onto
    /// the line.
    /// <para>
    /// Driven off the rows rather than off what this pass created, which is what
    /// makes running it again always safe and always progress. Saved after each
    /// enqueue rather than at the end: the queue write has already happened and is
    /// idempotent under the line's key, so the worst a crash mid-loop can do is
    /// leave a line whose job exists and whose id was not recorded — and the next
    /// pass re-enqueues under the same key, gets the same task back, and records it.
    /// </para>
    /// </summary>
    private async Task<Result<CountPlan>> RaiseOutstandingAsync(
        CountPlan plan,
        CancellationToken cancellationToken)
    {
        var outstanding = plan.Lines.Where(line => line.WorkTaskId is null).ToList();

        foreach (var line in outstanding)
        {
            var raised = await queue.EnqueueAsync(
                new WorkTaskRequest
                {
                    Kind = WorkTaskKind.CycleCount,
                    WarehouseId = plan.WarehouseId,
                    ItemId = line.ItemId,
                    LotId = line.LotId,
                    Quantity = line.ExpectedQuantity,
                    FromLocationId = line.LocationId,

                    // The bay to go to. The same location as the origin, because a
                    // count does not move anything — which is why the handheld's
                    // "go here" line needs no new concept to render one.
                    SuggestedLocationId = line.LocationId,

                    LicensePlateId = line.LicensePlateId,
                    DecisionTraceId = plan.DecisionTraceId,
                    Condition = line.Condition,
                    SourceReference = $"{plan.Reference}#{line.Rank}",
                    IdempotencyKey = line.IdempotencyKey
                },
                cancellationToken);

            if (raised.IsFailure)
            {
                // Left for the next pass rather than failing the plan. Some of the
                // counts are out there and are worth doing; a plan that rolled
                // itself back because the twelfth job would not raise would throw
                // away eleven good ones.
                logger.LogError(
                    "Count plan {Reference} could not raise a job for {Location}: {Code}",
                    plan.Reference,
                    line.LocationCode,
                    raised.Error.Code);

                continue;
            }

            line.WorkTaskId = raised.Value;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (outstanding.Count > 0)
        {
            logger.LogInformation(
                "Count plan {Reference} raised {Raised} of {Outstanding} outstanding jobs",
                plan.Reference,
                outstanding.Count(line => line.WorkTaskId is not null),
                outstanding.Count);
        }

        return plan;
    }

    /// <summary>
    /// The holdings a counter is already on the way to.
    /// <para>
    /// Passed to the policy as a candidate exclusion rather than filtered out of
    /// the candidate query, so the trace can say a badly overdue bay was considered
    /// and is already on somebody's list — which is a completely different answer
    /// from "it was not considered".
    /// </para>
    /// <para>
    /// A line with no job yet counts as scheduled. It is a count this module has
    /// committed to and has not yet raised, and proposing it again in the same
    /// breath would be the duplicate the whole exclusion exists to prevent.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> OpenScheduledBalancesAsync(
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        var lines = await dbContext.CountPlanLines
            .AsNoTracking()
            .Where(line => line.Plan!.WarehouseId == warehouseId
                           && line.Plan.Status == CountPlanStatus.Open)
            .Select(line => new { line.BalanceId, line.WorkTaskId })
            .ToListAsync(cancellationToken);

        if (lines.Count == 0)
        {
            return [];
        }

        var states = await queue.FindManyAsync(
            [.. lines.Where(l => l.WorkTaskId is not null).Select(l => l.WorkTaskId!.Value)],
            cancellationToken);

        return
        [
            .. lines
                .Where(line => line.WorkTaskId is not { } taskId
                               || states.GetValueOrDefault(taskId) is null or
                                   { State: WorkTaskState.Ready or WorkTaskState.Assigned })
                .Select(line => line.BalanceId)
                .Distinct()
        ];
    }

    private async Task<CountPlan?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.CountPlans
            .Include(p => p.Lines)
            .SingleOrDefaultAsync(p => p.IdempotencyKey == key, cancellationToken);

    /// <summary>
    /// The next plan number in the warehouse's own book for the year.
    /// <para>
    /// Counted rather than held in a sequence, and racy on purpose: two supervisors
    /// planning in the same second collide on the unique index, and the loser
    /// converges on the winner's plan through the duplicate-key path above. A
    /// database sequence would avoid the collision and would also leave gaps
    /// whenever a plan failed to save, which is worse — a reference people quote
    /// out loud should not skip.
    /// </para>
    /// </summary>
    private async Task<string> NextReferenceAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var year = clock.GetUtcNow().Year;
        var prefix = $"CNT-{year.ToString(CultureInfo.InvariantCulture)}-";

        var used = await dbContext.CountPlans
            .AsNoTracking()
            .Where(p => p.WarehouseId == warehouseId && p.Reference.StartsWith(prefix))
            .CountAsync(cancellationToken);

        return $"{prefix}{(used + 1).ToString("D4", CultureInfo.InvariantCulture)}";
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
