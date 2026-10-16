using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Work.Features;

public sealed record ReslotPutawayRequest(string Reason);
public sealed record PutawayDecisionRevisionResponse(long Revision, string IdempotencyKey, string Reason,
    Guid? PreviousDecisionTraceId, Guid DecisionTraceId, Guid? SuggestedLocationId,
    DateTimeOffset DecidedAt, Guid UserId, string UserName, IReadOnlyList<PlateLine> Contents);
public sealed record ReslotPutawayResponse(WorkTaskDetailResponse Current,
    PutawayDecisionRevisionResponse AppliedRevision, bool WasAlreadyApplied);

public static class ReslotPutaway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tasks/{id:guid}/reslot", async (
            Guid id, ReslotPutawayRequest request, ClaimsPrincipal user,
            [FromHeader(Name = "Idempotency-Key")] string? key, WorkDbContext db,
            WorkTaskDescriber describe, IStockQuery stock, IPalletPutawayAdvisor advisor,
            IDecisionTraceReader traces, TimeProvider clock, CancellationToken ct) =>
            (await db.Database.CreateExecutionStrategy().ExecuteAsync(() =>
                HandleAsync(id, request, user, key, db, describe, stock, advisor, traces, clock, ct))).ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Work").WithName("ReslotPutaway")
            .WithSummary("Re-evaluate an unclaimed pallet and append its new explanation.")
            .Produces<ReslotPutawayResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
    }

    private static async Task<Result<ReslotPutawayResponse>> HandleAsync(Guid id, ReslotPutawayRequest request,
        ClaimsPrincipal user, string? key, WorkDbContext db, WorkTaskDescriber describe,
        IStockQuery stock, IPalletPutawayAdvisor advisor, IDecisionTraceReader traces,
        TimeProvider clock, CancellationToken ct)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || reason.Length is 0 or > 500)
            return Result.Failure<ReslotPutawayResponse>(WorkErrors.ReslotRequestInvalid);
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<ReslotPutawayResponse>(actor.Error);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = (await db.WorkTasks.FromSqlInterpolated(
            $"SELECT * FROM work.\"WorkTasks\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (task is null) return Result.Failure<ReslotPutawayResponse>(WorkErrors.TaskNotFound);
        var revision = await db.PutawayDecisionRevisions.SingleOrDefaultAsync(
            row => row.WorkTaskId == id && row.IdempotencyKey == key, ct);
        var wasAlready = revision is not null;
        if (revision is not null && revision.Reason != reason)
            return Result.Failure<ReslotPutawayResponse>(WorkErrors.ReslotKeyConflict);
        if (revision is null)
        {
            if (task.PendingPutawayCompletionId is not null)
                return Result.Failure<ReslotPutawayResponse>(WorkErrors.CompletionPending);
            if (task.Kind != WorkTaskKind.Putaway || task.LicensePlateId is not { } plateId)
                return Result.Failure<ReslotPutawayResponse>(WorkErrors.NotPalletPutaway);
            if (task.Status == WorkTaskStatus.Assigned)
                return Result.Failure<ReslotPutawayResponse>(WorkErrors.TaskAlreadyClaimed);
            if (task.Status != WorkTaskStatus.Ready)
                return Result.Failure<ReslotPutawayResponse>(WorkErrors.TaskNotOpen);
            if (task.PendingReceiptCount > 0)
                return Result.Failure<ReslotPutawayResponse>(WorkErrors.ReceiptPending);
            var contents = await stock.PlateContentsAsync(plateId, task.FromLocationId, ct);
            if (contents.Count == 0) return Result.Failure<ReslotPutawayResponse>(WorkErrors.PalletEmpty);
            var advice = await advisor.SuggestAsync(new PalletPutawayQuery
            {
                WarehouseId = task.WarehouseId, FromLocationId = task.FromLocationId,
                LicensePlateId = plateId, Contents = contents, Actor = actor.Value
            }, ct);
            if (advice.IsFailure) return Result.Failure<ReslotPutawayResponse>(advice.Error);
            revision = new PutawayDecisionRevision
            {
                WorkTaskId = id, Revision = task.PutawayRevision + 1, IdempotencyKey = key,
                Reason = reason, PreviousDecisionTraceId = task.DecisionTraceId,
                DecisionTraceId = advice.Value.DecisionTraceId, SuggestedLocationId = advice.Value.LocationId,
                ContentsJson = JsonSerializer.Serialize(contents, Json), DecidedAt = clock.GetUtcNow(),
                UserId = actor.Value.UserId, UserName = actor.Value.UserName
            };
            db.PutawayDecisionRevisions.Add(revision);
            task.Quantity = contents.Sum(line => line.Quantity);
            task.PutawayRevision = revision.Revision;
            task.DecisionTraceId = revision.DecisionTraceId;
            task.SuggestedLocationId = revision.SuggestedLocationId;
            task.PutawayAdviceStale = false;
            await db.SaveChangesAsync(ct);
        }
        var decision = task.DecisionTraceId is { } traceId ? await traces.FindAsync(traceId, ct) : null;
        var current = new WorkTaskDetailResponse(await describe.DescribeAsync(task, ct), decision,
            await HistoryAsync(db, id, task.PutawayRevision, ct));
        var response = new ReslotPutawayResponse(current, Describe(revision), wasAlready);
        await transaction.CommitAsync(ct);
        return response;
    }

    internal static async Task<IReadOnlyList<PutawayDecisionRevisionResponse>> HistoryAsync(WorkDbContext db, Guid taskId, long throughRevision, CancellationToken ct) =>
        (await db.PutawayDecisionRevisions.AsNoTracking().Where(row => row.WorkTaskId == taskId && row.Revision <= throughRevision)
            .OrderBy(row => row.Revision).ToListAsync(ct)).Select(Describe).ToArray();

    private static PutawayDecisionRevisionResponse Describe(PutawayDecisionRevision row) => new(
        row.Revision, row.IdempotencyKey, row.Reason, row.PreviousDecisionTraceId, row.DecisionTraceId,
        row.SuggestedLocationId, row.DecidedAt, row.UserId, row.UserName,
        Contents(row));

    internal static IReadOnlyList<PlateLine> Contents(PutawayDecisionRevision row) =>
        JsonSerializer.Deserialize<PlateLine[]>(row.ContentsJson, Json)
            ?? throw new InvalidOperationException("A putaway revision has no recorded contents.");
}
