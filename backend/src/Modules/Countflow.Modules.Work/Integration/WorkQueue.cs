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
    /// One open task per pallet per origin.
    /// <para>
    /// A trailer is unloaded SKU by SKU onto one pallet, so a mixed pallet arrives
    /// as several receipts — but it is one thing an operator picks up and moves
    /// once. A task per receipt would send three people to the same pallet, and
    /// the two who arrived second would find it gone. The whole-plate move at
    /// completion is the other half of the same idea: the pallet is the unit of
    /// work, and the ledger works out what is on it.
    /// </para>
    /// <para>
    /// The suggestion and its trace stay as they were. The rules chose a home for
    /// the first thing on the pallet, and re-deciding for a pallet whose contents
    /// are still growing would rewrite an explanation somebody may already have
    /// been shown. Re-slotting a mixed pallet as a whole is Phase 4 work.
    /// </para>
    /// </summary>
    private async Task<WorkTask?> FindOpenPlateTaskAsync(
        WorkTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LicensePlateId is not { } plateId)
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
            Condition = request.Condition,
            SourceReference = request.SourceReference
        };

        dbContext.WorkTasks.Add(task);

        return task;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
