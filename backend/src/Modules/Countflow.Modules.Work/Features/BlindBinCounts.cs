using System.Security.Claims;
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

public sealed record CreateBinCountRequest(string LocationCode, string Reason);
public sealed record StartBinCountRequest(bool Restart = false);

public static class BlindBinCounts
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/bin-counts", async (CreateBinCountRequest request, ClaimsPrincipal user,
            [FromHeader(Name = "Idempotency-Key")] string? key, ILocationDirectory locations, IWorkQueue queue,
            WorkDbContext db, WorkTaskDescriber describe, CancellationToken ct) =>
        {
            var actor = PostingActor.From(user);
            if (actor.IsFailure) return actor.ToHttpResult();
            if (string.IsNullOrWhiteSpace(key)) return Result.Failure(WorkErrors.IdempotencyKeyRequired).ToHttpResult();
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 120)
                return Result.Failure(Error.Validation("work.bin_count_reason_required", "Enter a reason of up to 120 characters.")).ToHttpResult();
            var location = await locations.FindStorageLocationByCodeAsync(request.LocationCode.Trim(), ct);
            if (location is null || !location.IsActive || !location.IsHierarchyActive
                || location.Role is not (StorageRole.Reserve or StorageRole.Forward or StorageRole.Hold or StorageRole.Staging))
                return Result.Failure(WorkErrors.LocationNotFound).ToHttpResult();
            var source = $"{actor.Value.UserId:D}: {request.Reason.Trim()}";
            var created = await queue.EnqueueAsync(new WorkTaskRequest { Kind = WorkTaskKind.BinCount,
                WarehouseId = location.WarehouseId, ItemId = null, Quantity = 0, FromLocationId = location.Id,
                SuggestedLocationId = location.Id, SourceReference = source, IdempotencyKey = key }, ct);
            if (created.IsFailure) return Result.Failure(created.Error).ToHttpResult();
            var task = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == created.Value, ct);
            if (task.Kind != WorkTaskKind.BinCount || task.FromLocationId != location.Id || task.SourceReference != source)
                return Result.Failure(Error.Conflict("work.idempotency_key_reused", "This key names a different bin count.")).ToHttpResult();
            return Results.Ok(await describe.DescribeAsync(task, ct));
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Work").WithName("CreateBinCount").WithSummary("Raise a blind count of an entire physical bin.")
            .Produces<WorkTaskResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);

        endpoints.MapPost("/work/tasks/{id:guid}/bin-count/start", async (Guid id, StartBinCountRequest request,
            ClaimsPrincipal user, WorkDbContext db, IBlindBinCounting inventory, WorkTaskDescriber describe, CancellationToken ct) =>
        {
            var actor = PostingActor.From(user);
            if (actor.IsFailure) return actor.ToHttpResult();
            var result = await InitializeAsync(id, actor.Value, request.Restart, db, inventory, ct);
            return result.IsFailure ? Result.Failure(result.Error).ToHttpResult()
                : Results.Ok(new WorkTaskDetailResponse(await describe.DescribeAsync(result.Value, ct), null));
        }).RequireAuthorization().WithTags("Work").WithName("StartBinCount")
            .WithSummary("Start or restart the assigned blind count after a stock change.")
            .Produces<WorkTaskDetailResponse>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }

    internal static Task<Result<WorkTask>> InitializeAsync(Guid id, PostingActor actor, bool restart,
        WorkDbContext db, IBlindBinCounting inventory, CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await db.WorkTasks.FromSqlInterpolated($"SELECT * FROM work.\"WorkTasks\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (task is null) return Result.Failure<WorkTask>(WorkErrors.TaskNotFound);
        if (task.Kind != WorkTaskKind.BinCount || task.Status != WorkTaskStatus.Assigned)
            return Result.Failure<WorkTask>(WorkErrors.TaskNotOpen);
        if (task.AssignedToUserId != actor.UserId) return Result.Failure<WorkTask>(WorkErrors.TaskNotYours);
        if (restart && await inventory.ReadConfirmationAsync(id, ct) is not null)
            return Result.Failure<WorkTask>(Error.Conflict("work.bin_count_recorded", "The count is recorded. Retry the saved confirmation."));
        if (task.BinCountVersion is null || restart)
        {
            task.BinCountVersion = await inventory.ReadVersionAsync(task.FromLocationId, ct);
            task.BinCountClaimId = Guid.CreateVersion7(); await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct); return Result.Success(task);
    });

    internal static Task<Result> ReleaseAsync(Guid id, PostingActor actor, WorkDbContext db,
        IBlindBinCounting inventory, CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var task = await db.WorkTasks.FromSqlInterpolated($"SELECT * FROM work.\"WorkTasks\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (task is null) return Result.Failure(WorkErrors.TaskNotFound);
        if (task.Status != WorkTaskStatus.Assigned) return Result.Failure(WorkErrors.TaskNotOpen);
        if (task.AssignedToUserId != actor.UserId) return Result.Failure(WorkErrors.TaskNotYours);
        if (await inventory.ReadConfirmationAsync(id, ct) is not null)
            return Result.Failure(Error.Conflict("work.bin_count_recorded", "The count is recorded. Retry the saved confirmation."));
        task.Status = WorkTaskStatus.Ready; task.AssignedToUserId = null; task.AssignedToUserName = null;
        task.AssignedAt = null; task.BinCountVersion = null; task.BinCountClaimId = null;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Result.Success();
    });
}
