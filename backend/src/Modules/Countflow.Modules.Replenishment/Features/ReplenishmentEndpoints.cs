using Countflow.Modules.Replenishment.Domain;
using Countflow.Modules.Replenishment.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Replenishment.Features;

public static class ReplenishmentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/replenishment/pick-faces", async (string? warehouse, int? page, int? pageSize,
            ReplenishmentDbContext db, IWarehouseDirectory warehouses, ReplenishmentDescriber describe, CancellationToken cancellationToken) =>
        {
            var number = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 100);
            var query = db.PickFacePolicies.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(warehouse))
            {
                var site = await warehouses.FindByCodeAsync(warehouse.Trim(), cancellationToken);
                query = query.Where(policy => policy.WarehouseId == (site == null ? Guid.Empty : site.Id));
            }
            var total = await query.CountAsync(cancellationToken);
            var policies = await query.OrderBy(policy => policy.LocationId).Skip((number - 1) * size).Take(size).ToListAsync(cancellationToken);
            return Results.Ok(new PickFacePageResponse(await describe.PoliciesAsync(policies, cancellationToken), total, number, size));
        }).RequireAuthorization().WithTags("Replenishment").WithName("GetPickFacePolicies")
            .WithSummary("List pick-face min/max policies and incoming stock.").Produces<PickFacePageResponse>();

        endpoints.MapPut("/replenishment/pick-faces/{locationCode}", async (string locationCode, SavePickFaceRequest request,
            ReplenishmentDbContext db, ILocationDirectory locations, IItemDirectory items,
            ReplenishmentRunner runner, ReplenishmentDescriber describe, IStockAvailability availability,
            IWarehouseDirectory warehouses, IStockQuery stockQuery, CancellationToken cancellationToken) =>
        {
            const decimal ceiling = 1_000_000_000m;
            if (request.Minimum <= 0 || request.Maximum <= request.Minimum || request.Maximum > ceiling
                || decimal.Round(request.Minimum, 4) != request.Minimum || decimal.Round(request.Maximum, 4) != request.Maximum)
                return Result.Failure(ReplenishmentErrors.LevelsInvalid).ToHttpResult();
            var face = await locations.FindStorageLocationByCodeAsync(locationCode.Trim(), cancellationToken);
            if (face is not { Role: StorageRole.Forward, IsActive: true, IsReplenishable: true }
                || await warehouses.FindAsync(face.WarehouseId, cancellationToken) is not { IsActive: true })
                return Result.Failure(ReplenishmentErrors.FaceInvalid).ToHttpResult();
            var item = await items.FindBySkuAsync(request.Sku.Trim(), cancellationToken);
            if (item is not { IsActive: true }) return Result.Failure(ReplenishmentErrors.ItemNotFound).ToHttpResult();
            if (item.TemperatureBand != face.Temperature)
                return Result.Failure(ReplenishmentErrors.FaceInvalid).ToHttpResult();
            if ((item.UnitVolumeM3 is { } volume && face.MaxVolumeM3 is { } maxVolume && request.Maximum * volume > maxVolume)
                || (item.UnitWeightKg is { } weight && face.MaxWeightKg is { } maxWeight && request.Maximum * weight > maxWeight))
                return Result.Failure(ReplenishmentErrors.CapacityExceeded).ToHttpResult();
            await using var lease = await PickFaceLease.AcquireAsync(db, face.Id, cancellationToken);
            var policy = await db.PickFacePolicies.SingleOrDefaultAsync(row => row.LocationId == face.Id, cancellationToken);
            var occupancy = (await stockQuery.OccupancyAsync([face.Id], item.Id, null, cancellationToken)).GetValueOrDefault(face.Id);
            if (occupancy is not null && occupancy.TotalUnits > occupancy.SubjectItemUnits)
                return Result.Failure(ReplenishmentErrors.InProgress).ToHttpResult();
            if (policy is not null && request.Maximum < policy.Maximum)
            {
                var incoming = await runner.IncomingAsync(face.Id, cancellationToken);
                var stock = await availability.HoldingsAsync(face.WarehouseId, policy.ItemId, cancellationToken);
                if (policy.PendingPlanId != null || request.Maximum < incoming + stock.Where(holding => holding.LocationId == face.Id).Sum(holding => holding.OnHandQuantity))
                    return Result.Failure(ReplenishmentErrors.StockCommitted).ToHttpResult();
            }
            if (policy is not null && policy.ItemId != item.Id)
            {
                var incoming = await runner.IncomingAsync(face.Id, cancellationToken);
                var stock = await availability.HoldingsAsync(face.WarehouseId, policy.ItemId, cancellationToken);
                if (policy.PendingPlanId != null || incoming > 0 || stock.Any(holding => holding.LocationId == face.Id && holding.OnHandQuantity > 0))
                    return Result.Failure(ReplenishmentErrors.InProgress).ToHttpResult();
            }
            if (policy is null)
            {
                policy = new PickFacePolicy { LocationId = face.Id, WarehouseId = face.WarehouseId };
                db.PickFacePolicies.Add(policy);
            }
            policy.ItemId = item.Id;
            policy.Minimum = request.Minimum;
            policy.Maximum = request.Maximum;
            policy.Enabled = request.Enabled;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok((await describe.PoliciesAsync([policy], cancellationToken)).Single());
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Replenishment").WithName("SavePickFacePolicy").WithSummary("Save a pick-face min/max policy.")
            .Produces<PickFacePolicyResponse>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        endpoints.MapGet("/replenishment/plans/{id:guid}", async (Guid id, ReplenishmentDbContext db,
            ReplenishmentDescriber describe, CancellationToken cancellationToken) =>
        {
            var plan = await db.Plans.AsNoTracking().Include(plan => plan.Lines).SingleOrDefaultAsync(plan => plan.Id == id, cancellationToken);
            return plan is null ? Result.Failure(ReplenishmentErrors.PlanNotFound).ToHttpResult()
                : Results.Ok(await describe.PlanAsync(plan, cancellationToken));
        }).RequireAuthorization().WithTags("Replenishment").WithName("GetReplenishmentPlan")
            .WithSummary("Read a replenishment intent and its tasks.").Produces<ReplenishmentPlanResponse>().ProducesProblem(404);

        endpoints.MapPost("/replenishment/plans/{id:guid}/cancel", async (Guid id, ReplenishmentDbContext db,
            ReplenishmentRunner runner, IWorkQueue queue, IStockReservations reservations, CancellationToken cancellationToken) =>
        {
            var locationId = await db.Plans.Where(plan => plan.Id == id).Select(plan => (Guid?)plan.LocationId).SingleOrDefaultAsync(cancellationToken);
            if (locationId is null) return Result.Failure(ReplenishmentErrors.PlanNotFound).ToHttpResult();
            await using var lease = await PickFaceLease.AcquireAsync(db, locationId.Value, cancellationToken);
            var plan = await db.Plans.Include(plan => plan.Lines).SingleAsync(plan => plan.Id == id, cancellationToken);
            try { await runner.ResolveAsync(plan, cancellationToken); }
            catch (InvalidOperationException) { return Result.Failure(ReplenishmentErrors.RetryCancellation).ToHttpResult(); }
            var cancelled = await queue.CancelAsync(plan.Lines.Where(line => line.WorkTaskId != null).Select(line => line.WorkTaskId!.Value).ToList(),
                $"{plan.Reference} cancelled", cancellationToken);
            if (cancelled.IsFailure) return Result.Failure(cancelled.Error).ToHttpResult();
            var held = await reservations.FindOpenByOwnerAsync(plan.Id, cancellationToken);
            var released = await reservations.ReleaseAsync(held.Select(reservation => reservation.Id).ToList(), $"{plan.Reference} cancelled", cancellationToken);
            if (released.IsFailure) return Result.Failure(released.Error).ToHttpResult();
            plan.Status = ReplenishmentPlanStatus.Cancelled;
            var policy = await db.PickFacePolicies.SingleAsync(policy => policy.LocationId == plan.LocationId, cancellationToken);
            if (policy.PendingPlanId == plan.Id) policy.PendingPlanId = null;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new CancelReplenishmentResponse(plan.Id, cancelled.Value, released.Value));
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Replenishment").WithName("CancelReplenishmentPlan").WithSummary("Withdraw unclaimed replenishment work and release its stock.")
            .Produces<CancelReplenishmentResponse>().ProducesProblem(404).ProducesProblem(409);
    }
}
