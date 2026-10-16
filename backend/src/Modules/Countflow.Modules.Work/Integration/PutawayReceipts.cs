using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Work.Integration;

public sealed class PutawayReceipts(WorkDbContext dbContext, TimeProvider clock) : IPutawayReceipts
{
    public Task<Result> PrepareAsync(PutawayReceiptPreparation request, CancellationToken cancellationToken) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
                || request.Sscc.Length is 0 or > 18 || request.Fingerprint.Length != 64)
                return Result.Failure(WorkErrors.ReceiptInvalid);
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO work."PutawayReceiptLanes" ("Sscc", "LicensePlateId")
                VALUES ({request.Sscc}, {request.ExistingLicensePlateId})
                ON CONFLICT ("Sscc") DO NOTHING
                """, token);
            var lane = await LockLaneAsync(request.Sscc, token);
            var intent = await dbContext.PutawayReceiptIntents.SingleOrDefaultAsync(
                receipt => receipt.IdempotencyKey == request.IdempotencyKey, token);
            if (intent is not null && (intent.Fingerprint != request.Fingerprint
                || intent.Sscc != request.Sscc || intent.FromLocationId != request.FromLocationId
                || intent.WarehouseId != request.WarehouseId))
                return Result.Failure(WorkErrors.ReceiptKeyConflict);
            if (intent?.AcknowledgedAt is not null) return Result.Success();
            // A receipt acknowledged before this protocol was deployed has no
            // intent to bind. Preserve its replay path (including assigned or
            // completed tasks); Inventory still validates its original shape.
            if (intent is null && await dbContext.WorkTaskSources.AnyAsync(
                source => source.IdempotencyKey == request.IdempotencyKey, token))
                return Result.Success();
            if (lane.LicensePlateId is { } known && request.ExistingLicensePlateId is { } found && known != found)
                return Result.Failure(WorkErrors.ReceiptKeyConflict);
            lane.LicensePlateId ??= request.ExistingLicensePlateId;
            var task = await LockTaskAsync(dbContext, lane.LicensePlateId, request.FromLocationId, token);
            if (task?.PendingPutawayCompletionId is not null) return Result.Failure(WorkErrors.CompletionPending);
            if (task?.Status == WorkTaskStatus.Assigned) return Result.Failure(WorkErrors.PalletInUse);
            if (intent is null)
            {
                intent = new PutawayReceiptIntent { IdempotencyKey = request.IdempotencyKey,
                    Fingerprint = request.Fingerprint, Sscc = request.Sscc, WarehouseId = request.WarehouseId,
                    FromLocationId = request.FromLocationId, PreparedAt = clock.GetUtcNow() };
                dbContext.PutawayReceiptIntents.Add(intent);
            }
            intent.IsPending = true;
            await dbContext.SaveChangesAsync(token);
            if (task is not null)
            {
                task.PendingReceiptCount = await PendingCountAsync(request.Sscc, request.FromLocationId, null, token);
                await dbContext.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, cancellationToken);

    public async Task AbortAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
            var sscc = await dbContext.PutawayReceiptIntents.Where(receipt => receipt.IdempotencyKey == idempotencyKey)
                .Select(receipt => receipt.Sscc).SingleOrDefaultAsync(token);
            if (sscc is null) return;
            var lane = await LockLaneAsync(sscc, token);
            var intent = await dbContext.PutawayReceiptIntents.SingleAsync(receipt => receipt.IdempotencyKey == idempotencyKey, token);
            if (!intent.IsPending || intent.AcknowledgedAt is not null) return;
            var task = await LockTaskAsync(dbContext, lane.LicensePlateId, intent.FromLocationId, token);
            intent.IsPending = false;
            if (task is not null)
                task.PendingReceiptCount = await PendingCountAsync(sscc, intent.FromLocationId, idempotencyKey, token);
            await dbContext.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }, cancellationToken);
    }

    internal async Task<Result<PutawayReceiptIntent?>> LockAcknowledgementAsync(WorkTaskRequest request, CancellationToken token)
    {
        var sscc = await dbContext.PutawayReceiptIntents.Where(receipt => receipt.IdempotencyKey == request.IdempotencyKey)
            .Select(receipt => receipt.Sscc).SingleOrDefaultAsync(token);
        if (sscc is null) return Result.Success<PutawayReceiptIntent?>(null);
        var lane = await LockLaneAsync(sscc, token);
        var intent = await dbContext.PutawayReceiptIntents.SingleAsync(receipt => receipt.IdempotencyKey == request.IdempotencyKey, token);
        if (!intent.IsPending || request.Kind != WorkTaskKind.Putaway || request.LicensePlateId is null
            || intent.FromLocationId != request.FromLocationId || intent.WarehouseId != request.WarehouseId
            || (lane.LicensePlateId is { } existing && existing != request.LicensePlateId))
            return Result.Failure<PutawayReceiptIntent?>(WorkErrors.ReceiptKeyConflict);
        lane.LicensePlateId ??= request.LicensePlateId;
        return intent;
    }

    internal async Task AcknowledgeAsync(PutawayReceiptIntent intent, WorkTask task, CancellationToken token)
    {
        intent.IsPending = false;
        intent.AcknowledgedAt = clock.GetUtcNow();
        task.PendingReceiptCount = await PendingCountAsync(intent.Sscc, intent.FromLocationId, intent.IdempotencyKey, token);
    }

    private Task<PutawayReceiptLane> LockLaneAsync(string sscc, CancellationToken token) =>
        dbContext.PutawayReceiptLanes.FromSqlInterpolated(
            $"SELECT * FROM work.\"PutawayReceiptLanes\" WHERE \"Sscc\" = {sscc} FOR UPDATE").SingleAsync(token);

    private Task<int> PendingCountAsync(string sscc, Guid origin, string? excludingKey, CancellationToken token) =>
        dbContext.PutawayReceiptIntents.CountAsync(receipt => receipt.Sscc == sscc && receipt.FromLocationId == origin
            && receipt.IsPending && receipt.IdempotencyKey != excludingKey, token);

    internal static async Task<WorkTask?> LockTaskAsync(WorkDbContext dbContext, Guid? plateId, Guid origin, CancellationToken token)
    {
        if (plateId is null) return null;
        return (await dbContext.WorkTasks.FromSqlInterpolated($"""
            SELECT * FROM work."WorkTasks" WHERE "LicensePlateId" = {plateId}
              AND "FromLocationId" = {origin} AND "Kind" = {(int)WorkTaskKind.Putaway}
              AND "Status" IN ({(int)WorkTaskStatus.Ready}, {(int)WorkTaskStatus.Assigned})
            ORDER BY "CreatedAt" LIMIT 1 FOR UPDATE
            """).ToListAsync(token)).SingleOrDefault();
    }
}
