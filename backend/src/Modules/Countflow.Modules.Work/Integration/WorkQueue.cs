using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Countflow.Modules.Work.Integration;

/// <summary>
/// Work's published surface for raising directed work (ADR 0002).
/// <para>
/// The only thing another module may do to the queue is add to it. Claiming,
/// completing and cancelling are operator actions and belong to this module's
/// endpoints — a contract that let Inbound complete a task would let Inbound move
/// stock without an operator ever confirming anything.
/// </para>
/// </summary>
public sealed class WorkQueue(
    WorkDbContext dbContext,
    TimeProvider clock,
    ILogger<WorkQueue> logger) : IWorkQueue
{
    public async Task<Result<Guid>> EnqueueAsync(
        WorkTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Result.Failure<Guid>(WorkErrors.IdempotencyKeyRequired);
        }

        if (request.Quantity <= 0m)
        {
            return Result.Failure<Guid>(WorkErrors.QuantityNotPositive);
        }

        // EnableRetryOnFailure forbids hand-rolled transactions, so the whole unit
        // goes to the execution strategy. It needs to be one: a source row without
        // its task, or a task whose quantity was never folded in, are both states
        // nothing downstream knows how to read.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            token => EnqueueInTransactionAsync(request, isRetry: false, token),
            cancellationToken);
    }

    private async Task<Result<Guid>> EnqueueInTransactionAsync(
        WorkTaskRequest request,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var already = await dbContext.WorkTaskSources
                .AsNoTracking()
                .Where(s => s.IdempotencyKey == request.IdempotencyKey)
                .Select(s => (Guid?)s.WorkTaskId)
                .SingleOrDefaultAsync(cancellationToken);

            if (already is { } existing)
            {
                return existing;
            }

            var open = await FindOpenPlateTaskAsync(request, cancellationToken);
            var task = open ?? Create(request);

            dbContext.WorkTaskSources.Add(new WorkTaskSource
            {
                WorkTaskId = task.Id,
                IdempotencyKey = request.IdempotencyKey,
                Quantity = request.Quantity,
                AddedAt = clock.GetUtcNow()
            });

            task.Quantity += request.Quantity;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "{Verb} {Kind} task {TaskId}: {Quantity} of item {ItemId} at {From} (suggestion {Suggested}, trace {TraceId})",
                open is null ? "Raised" : "Merged into",
                request.Kind,
                task.Id,
                request.Quantity,
                request.ItemId,
                request.FromLocationId,
                request.SuggestedLocationId,
                request.DecisionTraceId);

            return task.Id;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Two copies of one retried receipt arrived close enough together that
            // both got past the checks above, or two receipts raced to be the first
            // task for a pallet. Either way a unique index settled it, and the
            // loser reports the winner's task — the same answer the caller would
            // have got had it arrived a moment later.
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var winner = await dbContext.WorkTaskSources
                .AsNoTracking()
                .Where(s => s.IdempotencyKey == request.IdempotencyKey)
                .Select(s => (Guid?)s.WorkTaskId)
                .SingleOrDefaultAsync(cancellationToken);

            if (winner is { } settled)
            {
                return settled;
            }

            // Not our own key, so it was the pallet index: another receipt created
            // the task for this plate a moment before us. One retry, which now
            // finds it and merges. Bounded, because a second failure means
            // something other than a race and a loop would hide it.
            return isRetry
                ? Result.Failure<Guid>(WorkErrors.EnqueueRaceLost)
                : await EnqueueInTransactionAsync(request, isRetry: true, cancellationToken);
        }
    }

    /// <summary>
    /// One open putaway per pallet per origin.
    /// <para>
    /// A trailer is unloaded SKU by SKU onto one pallet, so a mixed pallet arrives
    /// as several receipts — but it is one thing an operator picks up and moves
    /// once. A task per receipt would send three people to the same pallet, and
    /// the two who arrived second would find it gone. The whole-plate move at
    /// completion is the other half of the same idea: the pallet is the unit of
    /// work, and the ledger works out what is on it.
    /// </para>
    /// <para>
    /// <b>Only for putaway</b>, and Phase 4 is where that stopped being obvious. A
    /// pick names a pallet as well, because the operator has to be told which one
    /// to break into — but the unit of work there is the order line, not the
    /// pallet. Merging two orders' picks off one pallet would produce a single job
    /// whose confirmation could only be right for one of them. The kind decides
    /// what the unit of work is, and merging follows the unit of work.
    /// </para>
    /// <para>
    /// The suggestion and its trace stay as they were. The rules chose a home for
    /// the first thing on the pallet, and re-deciding for a pallet whose contents
    /// are still growing would rewrite an explanation somebody may already have
    /// been shown. Re-slotting a mixed pallet as a whole is still deferred.
    /// </para>
    /// </summary>
    private async Task<WorkTask?> FindOpenPlateTaskAsync(
        WorkTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind != WorkTaskKind.Putaway || request.LicensePlateId is not { } plateId)
        {
            return null;
        }

        return await dbContext.WorkTasks
            .Include(t => t.Sources)
            .Where(t => t.LicensePlateId == plateId
                        && t.FromLocationId == request.FromLocationId
                        && t.Kind == request.Kind
                        && (t.Status == WorkTaskStatus.Ready || t.Status == WorkTaskStatus.Assigned))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkTaskSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<Guid, WorkTaskSummary>();
        }

        var ids = taskIds.Distinct().ToArray();

        var rows = await dbContext.WorkTasks
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new Row(
                t.Id, t.Kind, t.Status, t.Quantity,
                t.CompletedAtLocationId, t.CompletedAt, t.CompletedByUserName, t.WasOverridden,
                t.CountedQuantity))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => new WorkTaskSummary(
                row.Id,
                row.Kind,
                ToState(row.Status),
                row.Quantity,
                row.CompletedAtLocationId,
                row.CompletedAt,
                row.CompletedByUserName,
                row.WasOverridden,
                row.CountedQuantity));
    }

    /// <summary>
    /// Withdraws work nobody has started.
    /// <para>
    /// Filtered to <see cref="WorkTaskStatus.Ready"/> in the predicate rather than
    /// checked and then updated, so a task somebody claims in the same instant is
    /// simply not among the rows that change. The count going back tells the
    /// caller how much of its work it failed to withdraw, which is what a
    /// cancelled order needs to know before it lets the stock go — releasing a
    /// reservation an operator is at that moment walking towards would send them
    /// to pick against a claim that no longer exists.
    /// </para>
    /// </summary>
    public async Task<Result<int>> CancelAsync(
        IReadOnlyCollection<Guid> taskIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        var ids = taskIds.Distinct().ToArray();

        var cancelled = await dbContext.WorkTasks
            .Where(t => ids.Contains(t.Id) && t.Status == WorkTaskStatus.Ready)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(t => t.Status, WorkTaskStatus.Cancelled)
                    .SetProperty(t => t.CancelledReason, reason),
                cancellationToken);

        if (cancelled > 0)
        {
            logger.LogInformation("Cancelled {Count} queued tasks: {Reason}", cancelled, reason);
        }

        return cancelled;
    }

    private sealed record Row(
        Guid Id,
        WorkTaskKind Kind,
        WorkTaskStatus Status,
        decimal Quantity,
        Guid? CompletedAtLocationId,
        DateTimeOffset? CompletedAt,
        string? CompletedByUserName,
        bool WasOverridden,
        decimal? CountedQuantity);

    /// <summary>
    /// The internal status and the published state are separate enums with
    /// identical members, for the same reason <c>InventoryStatus</c> and
    /// <c>StockCondition</c> are: they are allowed to diverge, and the mapping is
    /// where that stays visible.
    /// </summary>
    private static WorkTaskState ToState(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Assigned => WorkTaskState.Assigned,
        WorkTaskStatus.Completed => WorkTaskState.Completed,
        WorkTaskStatus.Cancelled => WorkTaskState.Cancelled,
        _ => WorkTaskState.Ready
    };

    private WorkTask Create(WorkTaskRequest request)
    {
        var task = new WorkTask
        {
            Kind = request.Kind,
            WarehouseId = request.WarehouseId,
            Priority = request.Priority,
            ItemId = request.ItemId,
            LotId = request.LotId,
            Quantity = 0m,
            FromLocationId = request.FromLocationId,
            SuggestedLocationId = request.SuggestedLocationId,
            LicensePlateId = request.LicensePlateId,
            DecisionTraceId = request.DecisionTraceId,
            StockReservationId = request.StockReservationId,
            Condition = request.Condition,
            SourceReference = request.SourceReference
        };

        dbContext.WorkTasks.Add(task);

        return task;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
