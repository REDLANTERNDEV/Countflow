using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Work.Features;

/// <summary>
/// Work commits its intent before Inventory posts. No Work transaction spans the
/// Inventory call; recovery reuses the frozen command, never today's task advice.
/// Inventory's terminal outcome makes both success and refusal safe to settle.
/// </summary>
public sealed class PutawayCompletions(WorkDbContext db, WorkTaskDescriber describe,
    IStockPosting posting, ILocationDirectory locations, TimeProvider clock, ILogger<PutawayCompletions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<CompleteWorkTaskResponse>> CompleteAsync(Guid id, CompleteWorkTaskRequest request,
        ClaimsPrincipal user, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.IdempotencyKeyRequired);
        if (key.Length > 124 || request.OverrideReason?.Length > 500 || string.IsNullOrWhiteSpace(request.ToLocationCode))
            return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.CompletionRequestInvalid);
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<CompleteWorkTaskResponse>(actor.Error);
        var normalized = request with { ToLocationCode = request.ToLocationCode.Trim() };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Request = normalized, Actor = actor.Value.UserId }, Json))));
        var prepared = await PrepareAsync(id, normalized, key, fingerprint, actor.Value, ct);
        if (prepared.IsFailure) return Result.Failure<CompleteWorkTaskResponse>(prepared.Error);
        if (prepared.Value.Reply is { } reply) return reply;
        var snapshot = prepared.Value.Snapshot!;
        // Exceptions deliberately leave the committed intent pending. The original
        // key/body resumes even when Inventory committed but its reply was lost.
        var result = await posting.PutawayLicensePlateAsync(snapshot.Instruction, ct);
        return await SettleAsync(id, prepared.Value.IntentId!.Value, snapshot, result, ct);
    }

    private async Task<Result<Prepared>> PrepareAsync(Guid id, CompleteWorkTaskRequest request, string key,
        string fingerprint, PostingActor actor, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await LockTaskAsync(id, ct);
        if (task is null) return Result.Failure<Prepared>(WorkErrors.TaskNotFound);
        var existing = await db.PutawayCompletionIntents.SingleOrDefaultAsync(
            intent => intent.WorkTaskId == id && intent.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            var saved = ReadSnapshot(existing);
            if (saved.Instruction.Actor.UserId != actor.UserId) return Result.Failure<Prepared>(WorkErrors.TaskNotYours);
            if (existing.Fingerprint != fingerprint) return Result.Failure<Prepared>(WorkErrors.CompletionKeyConflict);
            if (existing.State == PutawayCompletionState.Refused) return Result.Failure<Prepared>(ReadError(existing));
            if (existing.State == PutawayCompletionState.Succeeded)
                return new Prepared(null, null, new CompleteWorkTaskResponse(await describe.DescribeAsync(task, ct), []));
            if (task.PendingPutawayCompletionId != existing.Id)
                throw new InvalidOperationException("A pending pallet confirmation lost its task fence.");
            return new Prepared(existing.Id, saved);
        }
        // Honest upgrade behavior: an old completed task has no stored body to
        // reconstruct. Preserve its historical same-key replay without inventing one.
        if (task.Status == WorkTaskStatus.Completed)
            return task.CompletionIdempotencyKey == key
                ? new Prepared(null, null, new CompleteWorkTaskResponse(await describe.DescribeAsync(task, ct), []))
                : Result.Failure<Prepared>(WorkErrors.TaskAlreadyCompleted);
        if (task.PendingPutawayCompletionId is not null) return Result.Failure<Prepared>(WorkErrors.CompletionPending);
        if (task.PendingReceiptCount > 0) return Result.Failure<Prepared>(WorkErrors.ReceiptPending);
        if (task.Status == WorkTaskStatus.Cancelled) return Result.Failure<Prepared>(WorkErrors.TaskNotOpen);
        if (request.PickTourId is { } tour && task.PickTourId != tour) return Result.Failure<Prepared>(WorkErrors.TaskNotYours);
        if (task.AssignedToUserId is { } owner && owner != actor.UserId) return Result.Failure<Prepared>(WorkErrors.TaskNotYours);
        if (task.PutawayAdviceStale) return Result.Failure<Prepared>(WorkErrors.PutawayContentsChanged);
        if (task.PutawayRevision > 0 && request.ExpectedDecisionTraceId != task.DecisionTraceId)
            return Result.Failure<Prepared>(WorkErrors.PutawayDecisionChanged);
        if (task.DecisionTraceId is not { } traceId) return Result.Failure<Prepared>(WorkErrors.TaskHasNoDecision);
        var destination = await locations.FindByCodeAsync(request.ToLocationCode, ct);
        if (destination is null) return Result.Failure<Prepared>(WorkErrors.LocationNotFound);
        IReadOnlyList<PlateLine>? contents = null;
        if (task.PutawayRevision > 0)
        {
            var revision = await db.PutawayDecisionRevisions.AsNoTracking().SingleAsync(row =>
                row.WorkTaskId == id && row.Revision == task.PutawayRevision && row.DecisionTraceId == traceId, ct);
            contents = ReslotPutaway.Contents(revision);
        }
        // Null retains the legacy single-item decision's current-contents scope,
        // including replay after an older binary already emptied the origin.
        var snapshot = new Snapshot(new PlatePutawayInstruction
        {
            LicensePlateId = task.LicensePlateId!.Value, FromLocationId = task.FromLocationId,
            ToLocationId = destination.Id, TaskId = task.Id, DecisionTraceId = traceId,
            IdempotencyKey = key, Actor = actor, ExpectedContents = contents
        }, destination.Code, task.SuggestedLocationId != destination.Id, request.OverrideReason);
        var intent = new PutawayCompletionIntent
        {
            WorkTaskId = id, IdempotencyKey = key, Fingerprint = fingerprint,
            SnapshotJson = JsonSerializer.Serialize(snapshot, Json), PreparedAt = clock.GetUtcNow()
        };
        db.PutawayCompletionIntents.Add(intent);
        task.PendingPutawayCompletionId = intent.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new Prepared(intent.Id, snapshot);
    }

    private async Task<Result<CompleteWorkTaskResponse>> SettleAsync(Guid taskId, Guid intentId, Snapshot snapshot,
        Result<IReadOnlyList<PostedMovement>> result, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await LockTaskAsync(taskId, ct) ?? throw new InvalidOperationException("A confirming pallet task disappeared.");
        var intent = await db.PutawayCompletionIntents.SingleOrDefaultAsync(row => row.Id == intentId, ct);
        // Inventory rejected ownership of this key. Another identical caller may
        // already have removed our provisional attempt; never clear its successor.
        if (intent is null && result.IsFailure && result.Error.Code == "inventory.idempotency_key_reused")
            return Result.Failure<CompleteWorkTaskResponse>(result.Error);
        if (intent is null) throw new InvalidOperationException("A pallet confirmation intent disappeared.");
        if (intent.State == PutawayCompletionState.Refused) return Result.Failure<CompleteWorkTaskResponse>(ReadError(intent));
        if (intent.State == PutawayCompletionState.Succeeded)
            return new CompleteWorkTaskResponse(await describe.DescribeAsync(task, ct), []);
        if (task.PendingPutawayCompletionId != intentId || task.DecisionTraceId != snapshot.Instruction.DecisionTraceId)
            throw new InvalidOperationException("A pending pallet confirmation's task changed before settlement.");
        task.PendingPutawayCompletionId = null;
        intent.SettledAt = clock.GetUtcNow();
        if (result.IsFailure)
        {
            if (result.Error.Code == "inventory.idempotency_key_reused")
            {
                // The immutable Inventory command belongs to another instruction.
                // Do not bind Work's provisional key to a mistaken legacy retry.
                db.PutawayCompletionIntents.Remove(intent);
            }
            else
            {
                intent.State = PutawayCompletionState.Refused;
                intent.ErrorJson = JsonSerializer.Serialize(result.Error, Json);
            }
            if (task.PutawayRevision > 0 && result.Error.Code == "inventory.pallet_contents_changed") task.PutawayAdviceStale = true;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Failure<CompleteWorkTaskResponse>(result.Error);
        }
        intent.State = PutawayCompletionState.Succeeded;
        var actor = snapshot.Instruction.Actor;
        task.Status = WorkTaskStatus.Completed;
        task.CompletedAt = intent.SettledAt;
        task.CompletedAtLocationId = snapshot.Instruction.ToLocationId;
        task.CompletedByUserId = actor.UserId;
        task.CompletedByUserName = actor.UserName;
        task.CompletionIdempotencyKey = intent.IdempotencyKey;
        task.WasOverridden = snapshot.WasOverridden;
        task.OverrideReason = snapshot.WasOverridden ? snapshot.OverrideReason : null;
        task.AssignedToUserId ??= actor.UserId;
        task.AssignedToUserName ??= actor.UserName;
        task.AssignedAt ??= task.CompletedAt;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (snapshot.WasOverridden)
            logger.LogWarning("Task {TaskId} overridden by {User} at {Location}: {Reason}", taskId,
                actor.UserName, snapshot.DestinationCode, snapshot.OverrideReason ?? "no reason given");
        return new CompleteWorkTaskResponse(await describe.DescribeAsync(task, ct), result.Value);
    }

    private async Task<WorkTask?> LockTaskAsync(Guid id, CancellationToken ct) =>
        (await db.WorkTasks.FromSqlInterpolated($"SELECT * FROM work.\"WorkTasks\" WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
    private static Snapshot ReadSnapshot(PutawayCompletionIntent intent) =>
        JsonSerializer.Deserialize<Snapshot>(intent.SnapshotJson, Json) ?? throw new InvalidOperationException("A pallet confirmation snapshot is missing.");
    private static Error ReadError(PutawayCompletionIntent intent) =>
        JsonSerializer.Deserialize<Error>(intent.ErrorJson ?? "null", Json) ?? throw new InvalidOperationException("A refused pallet confirmation has no error.");
    private sealed record Prepared(Guid? IntentId, Snapshot? Snapshot, CompleteWorkTaskResponse? Reply = null);
    private sealed record Snapshot(PlatePutawayInstruction Instruction, string DestinationCode, bool WasOverridden, string? OverrideReason);
}
