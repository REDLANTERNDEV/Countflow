using System.Security.Claims;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Work.Features;

/// <summary>
/// The operator confirms the drop, and the ledger learns about it.
/// <para>
/// This is where Phase 3 closes the loop. The movement it posts carries three
/// things a Phase 2 movement could not: the task that directed it, the decision
/// trace that justified it, and — when the operator went somewhere else — the
/// fact that they did.
/// </para>
/// <para>
/// <b>Post first, then mark complete.</b> The two writes are in different schemas
/// and cannot share a transaction (ADR 0002), so one of them lands first and the
/// order is a choice. Posting first means a crash in between leaves a moved
/// pallet and an open task: the operator retries, the ledger recognises the
/// idempotency key and replays, and the task closes. The other order would leave
/// a task claiming work that never happened, which nothing can detect.
/// </para>
/// </summary>
public static class CompleteWorkTask
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tasks/{id:guid}/complete", async (
                Guid id,
                CompleteWorkTaskRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                IStockPosting posting,
                ILocationDirectory locations,
                TimeProvider clock,
                ILogger<WorkTask> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, request, user, idempotencyKey, dbContext, describe,
                        posting, locations, clock, logger, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("CompleteWorkTask")
            .WithSummary("Confirm a task at the location the operator actually scanned.")
            .Produces<CompleteWorkTaskResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<CompleteWorkTaskResponse>> HandleAsync(
        Guid id,
        CompleteWorkTaskRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        WorkDbContext dbContext,
        WorkTaskDescriber describe,
        IStockPosting posting,
        ILocationDirectory locations,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.IdempotencyKeyRequired);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<CompleteWorkTaskResponse>(actor.Error);

        var task = await dbContext.WorkTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null) return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.TaskNotFound);

        if (task.Status == WorkTaskStatus.Completed)
        {
            // The same key means the operator's handheld retried a confirmation
            // that had already landed, and the answer is the one it did not hear
            // the first time. A different key means a second confirmation of work
            // that is already done, which would move the stock twice.
            return string.Equals(task.CompletionIdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                ? new CompleteWorkTaskResponse(await describe.DescribeAsync(task, cancellationToken), [])
                : Result.Failure<CompleteWorkTaskResponse>(WorkErrors.TaskAlreadyCompleted);
        }

        if (task.Status == WorkTaskStatus.Cancelled)
        {
            return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.TaskNotOpen);
        }

        if (task.AssignedToUserId is { } owner && owner != actor.Value.UserId)
        {
            return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.TaskNotYours);
        }

        // ADR 0004, enforced at the last possible moment: a directed movement
        // without a trace is a bug, so rather than post one with a null we refuse
        // and say which invariant broke.
        if (task.DecisionTraceId is not { } decisionTraceId)
        {
            return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.TaskHasNoDecision);
        }

        var destination = await locations.FindByCodeAsync(request.ToLocationCode.Trim(), cancellationToken);
        if (destination is null) return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.LocationNotFound);

        var isCount = task.Kind == WorkTaskKind.CycleCount;

        if (isCount)
        {
            // A count names one holding, so scanning a different bay is a mis-scan
            // and not the judgement an override records. See WorkErrors.
            if (destination.Id != task.FromLocationId)
            {
                return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.CountLocationMismatch);
            }

            if (request.CountedQuantity is null)
            {
                return Result.Failure<CompleteWorkTaskResponse>(WorkErrors.CountQuantityRequired);
            }
        }

        var posted = await PostAsync(
            posting, task, request, destination.Id, decisionTraceId, idempotencyKey, actor.Value, cancellationToken);

        if (posted.IsFailure) return Result.Failure<CompleteWorkTaskResponse>(posted.Error);

        // A count has nothing to override: the operator is reporting a number rather
        // than choosing a destination, and their disagreement with the system is
        // recorded as the variance, which says far more than a boolean could.
        var overridden = !isCount && task.SuggestedLocationId != destination.Id;

        task.Status = WorkTaskStatus.Completed;
        task.CompletedAt = clock.GetUtcNow();
        task.CompletedAtLocationId = destination.Id;
        task.CompletedByUserId = actor.Value.UserId;
        task.CompletedByUserName = actor.Value.UserName;
        task.CompletionIdempotencyKey = idempotencyKey;
        task.CountedQuantity = isCount ? request.CountedQuantity : null;
        task.WasOverridden = overridden;
        task.OverrideReason = overridden ? request.OverrideReason : null;

        // An operator claiming a task by completing it straight off the queue is
        // normal on a quiet shift; record who did it either way.
        task.AssignedToUserId ??= actor.Value.UserId;
        task.AssignedToUserName ??= actor.Value.UserName;
        task.AssignedAt ??= task.CompletedAt;

        await dbContext.SaveChangesAsync(cancellationToken);

        if (overridden)
        {
            // Logged at warning, deliberately. An override is the measurable
            // disagreement between the rule set and the floor, and ADR 0004's
            // whole argument is that unexplained overrides becoming routine is
            // what degrades directed work. It should be visible without anyone
            // running a report.
            logger.LogWarning(
                "Task {TaskId} overridden: rules suggested {Suggested}, operator {User} confirmed {Actual} ({Reason})",
                task.Id,
                task.SuggestedLocationId,
                actor.Value.UserName,
                destination.Code,
                request.OverrideReason ?? "no reason given");
        }

        if (posted.Value.Count is { Variance: not 0m } variance)
        {
            // At warning for the same reason an override is: a variance is the
            // measurable disagreement between the record and the building, and the
            // whole of Phase 5's claim is that a warehouse should not have to run a
            // report to find out its numbers were wrong.
            logger.LogWarning(
                "Count variance on task {TaskId} at {Location}: book said {Expected}, {User} found {Counted} ({Variance})",
                task.Id,
                destination.Code,
                variance.ExpectedQuantity,
                actor.Value.UserName,
                variance.CountedQuantity,
                variance.Variance);
        }

        return new CompleteWorkTaskResponse(
            await describe.DescribeAsync(task, cancellationToken),
            posted.Value.Movements,
            posted.Value.Count);
    }

    /// <summary>What a confirmation produced: nought, one or several ledger lines, and a finding if it was a count.</summary>
    private sealed record Confirmation(IReadOnlyList<PostedMovement> Movements, CountOutcome? Count = null);

    /// <summary>
    /// What confirming a job means, by kind.
    /// <para>
    /// The one place in the module that knows the difference, and it is still a
    /// switch over which published operation to call. Everything else — the queue,
    /// the claim, the override recording, the handheld screen — is written against
    /// "go here, tell me what happened", which is what a putaway, a pick and a count
    /// all are. A second screen or a second endpoint per kind would have been a sign
    /// the task model was drawn wrong.
    /// </para>
    /// <para>
    /// A pallet moves whole; loose stock moves by the line. A putaway task
    /// carrying a licence plate is a task about a pallet, so it goes through the
    /// composite move — everything on that plate, in one transaction, under one
    /// key family. A pick is the opposite motion: it names the commitment it
    /// discharges and takes exactly what was promised off the plate.
    /// </para>
    /// <para>
    /// <b>And a count moves nothing.</b> Phase 5's addition is the branch that
    /// returns an empty list of movements as a success, which is why this method
    /// hands back a <see cref="Confirmation"/> rather than the bare list it used to:
    /// a count that agreed with the book has no ledger line to report and still has
    /// everything to say. Whether there is anything to post is Inventory's decision
    /// and not this module's — Work reports what the operator found and is told what
    /// became of it.
    /// </para>
    /// </summary>
    private static async Task<Result<Confirmation>> PostAsync(
        IStockPosting posting,
        WorkTask task,
        CompleteWorkTaskRequest request,
        Guid destinationId,
        Guid decisionTraceId,
        string idempotencyKey,
        PostingActor actor,
        CancellationToken cancellationToken)
    {
        if (task.Kind == WorkTaskKind.CycleCount)
        {
            // Guarded by the caller, which has already refused a count with no
            // number. Repeated as a pattern rather than a bang so a future caller
            // cannot quietly post a null as a zero — "found nothing" and "did not
            // answer" are the two things this whole feature must never confuse.
            if (request.CountedQuantity is not { } found)
            {
                return Result.Failure<Confirmation>(WorkErrors.CountQuantityRequired);
            }

            var counted = await posting.CountAsync(
                new CountInstruction
                {
                    ItemId = task.ItemId,
                    LotId = task.LotId,
                    LocationId = task.FromLocationId,
                    LicensePlateId = task.LicensePlateId,
                    Condition = task.Condition,
                    CountedQuantity = found,
                    IdempotencyKey = idempotencyKey,
                    Actor = actor,
                    TaskId = task.Id,
                    DecisionTraceId = decisionTraceId,
                    Notes = task.SourceReference
                },
                cancellationToken);

            if (counted.IsFailure) return Result.Failure<Confirmation>(counted.Error);

            return new Confirmation(
                counted.Value.Adjustment is { } adjustment ? [adjustment] : [],
                counted.Value);
        }

        if (task.Kind == WorkTaskKind.Pick)
        {
            // Refused rather than posted with a null, exactly as a directed
            // movement with no trace is. Stock leaving a bin against no claim is
            // an order being filled from inventory nobody promised it.
            if (task.StockReservationId is not { } reservationId)
            {
                return Result.Failure<Confirmation>(WorkErrors.TaskHasNoReservation);
            }

            var picked = await posting.PickAsync(
                new PickInstruction
                {
                    ReservationId = reservationId,
                    ToLocationId = destinationId,
                    IdempotencyKey = idempotencyKey,
                    Actor = actor,
                    TaskId = task.Id,
                    DecisionTraceId = decisionTraceId,
                    Notes = task.SourceReference
                },
                cancellationToken);

            return picked.IsFailure
                ? Result.Failure<Confirmation>(picked.Error)
                : new Confirmation([picked.Value]);
        }

        if (task.LicensePlateId is { } plateId)
        {
            var plate = await posting.PutawayLicensePlateAsync(
                new PlatePutawayInstruction
                {
                    LicensePlateId = plateId,
                    FromLocationId = task.FromLocationId,
                    ToLocationId = destinationId,
                    IdempotencyKey = idempotencyKey,
                    Actor = actor,
                    TaskId = task.Id,
                    DecisionTraceId = decisionTraceId
                },
                cancellationToken);

            return plate.IsFailure
                ? Result.Failure<Confirmation>(plate.Error)
                : new Confirmation(plate.Value);
        }

        var single = await posting.PutawayAsync(
            new PutawayInstruction
            {
                ItemId = task.ItemId,
                LotId = task.LotId,
                Quantity = task.Quantity,
                FromLocationId = task.FromLocationId,
                ToLocationId = destinationId,
                Condition = task.Condition,
                IdempotencyKey = idempotencyKey,
                Actor = actor,
                TaskId = task.Id,
                DecisionTraceId = decisionTraceId
            },
            cancellationToken);

        return single.IsFailure
            ? Result.Failure<Confirmation>(single.Error)
            : new Confirmation([single.Value]);
    }
}
