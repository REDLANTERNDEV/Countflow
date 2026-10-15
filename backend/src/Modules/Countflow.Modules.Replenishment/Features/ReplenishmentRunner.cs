using Countflow.Modules.Replenishment.Domain;
using Countflow.Modules.Replenishment.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Replenishment.Features;

public sealed class ReplenishmentCatalog(ReplenishmentDbContext db) : IBackgroundJobCatalog
{
    public async Task<IReadOnlyList<BackgroundJobRegistration>> ListAsync(CancellationToken cancellationToken)
    {
        var ids = await db.PickFacePolicies.AsNoTracking().Where(policy => policy.Enabled || policy.PendingPlanId != null)
            .Select(policy => policy.LocationId).ToListAsync(cancellationToken);
        return ids.Select(id => new BackgroundJobRegistration($"replenishment.{id:N}", typeof(ReplenishmentRunner),
            new RecurringJobOptions { Enabled = true, Interval = TimeSpan.FromMinutes(1), InitialDelay = TimeSpan.Zero })).ToList();
    }
}

public sealed class ReplenishmentRunner(ReplenishmentDbContext db, IStockAvailability availability,
    IAllocationAdvisor allocation, IStockReservations reservations, IWorkQueue queue,
    ILocationDirectory locations, IItemDirectory items, IWarehouseDirectory warehouses, IStockQuery stockQuery, TimeProvider clock, ILogger<ReplenishmentRunner> logger) : IBackgroundJob
{
    public const int MaxSourcesPerPlan = 50;
    private static readonly PostingActor Actor = new(Guid.Empty, "replenishment-scheduler");

    public async Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        var locationId = Guid.ParseExact(run.Name["replenishment.".Length..], "N");
        await using var lease = await PickFaceLease.AcquireAsync(db, locationId, cancellationToken);
        var policy = await db.PickFacePolicies.SingleOrDefaultAsync(row => row.LocationId == locationId, cancellationToken);
        if (policy is null) return;
        try { await RunCoreAsync(policy, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not DbUpdateConcurrencyException)
        {
            policy.LastErrorCode = "replenishment.retrying";
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task RunCoreAsync(PickFacePolicy policy, CancellationToken cancellationToken)
    {
        if (policy.PendingPlanId is { } pendingId)
        {
            var pending = await db.Plans.Include(plan => plan.Lines).SingleAsync(plan => plan.Id == pendingId, cancellationToken);
            await ResolveAsync(pending, cancellationToken);
            policy.PendingPlanId = null;
            policy.LastPlanId = pending.Id;
            policy.LastShortfall = pending.RequestedQuantity - pending.Lines.Where(line => !line.Refused).Sum(line => line.Quantity);
            policy.LastErrorCode = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (!policy.Enabled) return;
        var storage = await locations.FindStorageLocationsAsync([policy.LocationId], cancellationToken);
        var target = storage.GetValueOrDefault(policy.LocationId);
        var item = await items.FindAsync(policy.ItemId, cancellationToken);
        if (target is not { Role: StorageRole.Forward, IsActive: true, IsReplenishable: true }
            || item is not { IsActive: true }
            || await warehouses.FindAsync(policy.WarehouseId, cancellationToken) is not { IsActive: true }
            || item.TemperatureBand != target.Temperature)
        {
            policy.LastErrorCode = "replenishment.face_unavailable";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if ((item.UnitVolumeM3 is { } volume && target.MaxVolumeM3 is { } maxVolume && policy.Maximum * volume > maxVolume)
            || (item.UnitWeightKg is { } weight && target.MaxWeightKg is { } maxWeight && policy.Maximum * weight > maxWeight))
        {
            policy.LastErrorCode = "replenishment.capacity_exceeded";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        // Read work states before stock. Completion commits its inventory move
        // before its task state; reversing these reads can miss both the incoming
        // task and the newly arrived stock, causing an extra top-up.
        var incoming = await IncomingAsync(policy.LocationId, cancellationToken);
        var holdings = await availability.HoldingsAsync(policy.WarehouseId, policy.ItemId, cancellationToken);
        var face = holdings.Where(holding => holding.LocationId == policy.LocationId).ToList();
        var onHand = face.Sum(holding => holding.OnHandQuantity);
        var occupancy = (await stockQuery.OccupancyAsync([policy.LocationId], policy.ItemId, null, cancellationToken)).GetValueOrDefault(policy.LocationId);
        if (occupancy is not null && occupancy.TotalUnits > occupancy.SubjectItemUnits)
        {
            policy.LastErrorCode = "replenishment.face_unavailable";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        policy.LastCheckedAt = clock.GetUtcNow();
        var required = Math.Max(0, policy.Maximum - onHand - incoming);
        if (onHand + incoming >= policy.Minimum || required == 0)
        {
            policy.LastShortfall = Math.Min(policy.LastShortfall, required);
            policy.LastErrorCode = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var advice = await allocation.AllocateAsync(new AllocationQuery { WarehouseId = policy.WarehouseId,
            ItemId = policy.ItemId, Quantity = required, RequiredSourceRole = StorageRole.Reserve, Actor = Actor }, cancellationToken);
        if (advice.IsFailure) throw new InvalidOperationException(advice.Error.Code);
        policy.LastDecisionTraceId = advice.Value.DecisionTraceId;
        var selected = advice.Value.Lines.Take(MaxSourcesPerPlan).ToList();
        policy.LastShortfall = required - selected.Sum(line => line.Quantity);
        if (selected.Count == 0)
        {
            policy.LastErrorCode = "replenishment.no_source";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var plan = new ReplenishmentPlan { LocationId = policy.LocationId, WarehouseId = policy.WarehouseId,
            ItemId = policy.ItemId, DecisionTraceId = advice.Value.DecisionTraceId,
            RequestedQuantity = required, CreatedAt = clock.GetUtcNow() };
        foreach (var line in selected)
            plan.Lines.Add(new ReplenishmentLine { PlanId = plan.Id, BalanceId = line.BalanceId,
                FromLocationId = line.LocationId, LotId = line.LotId, LicensePlateId = line.LicensePlateId, Quantity = line.Quantity });
        db.Plans.Add(plan);
        policy.PendingPlanId = plan.Id;
        await db.SaveChangesAsync(cancellationToken);
        await ResolveAsync(plan, cancellationToken);
        policy.PendingPlanId = null;
        policy.LastPlanId = plan.Id;
        policy.LastShortfall = required - plan.Lines.Where(line => !line.Refused).Sum(line => line.Quantity);
        policy.LastErrorCode = null;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Replenishment {Reference} raised {Tasks} tasks for face {LocationId}; {Shortfall} short",
            plan.Reference, plan.Lines.Count(line => line.WorkTaskId != null), policy.LocationId, policy.LastShortfall);
    }

    internal async Task ResolveAsync(ReplenishmentPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Status != ReplenishmentPlanStatus.Open) return;
        foreach (var line in plan.Lines.Where(line => !line.Refused && line.WorkTaskId is null))
        {
            var key = $"replenishment:{line.Id:N}";
            if (line.ReservationId is null)
            {
                var held = await reservations.ReserveAsync(new ReservationInstruction { BalanceId = line.BalanceId,
                    Quantity = line.Quantity, OwnerId = plan.Id, OwnerReference = plan.Reference,
                    DecisionTraceId = plan.DecisionTraceId, IdempotencyKey = key, Actor = Actor }, cancellationToken);
                if (held.IsFailure)
                {
                    if (held.Error.Code is not ("inventory.insufficient_available_stock" or "inventory.holding_not_found"))
                        throw new InvalidOperationException(held.Error.Code);
                    line.Refused = true;
                    line.ErrorCode = held.Error.Code;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                line.ReservationId = held.Value.Id;
                await db.SaveChangesAsync(cancellationToken);
            }
            var task = await queue.EnqueueAsync(new WorkTaskRequest { Kind = WorkTaskKind.Replenishment,
                WarehouseId = plan.WarehouseId, ItemId = plan.ItemId, LotId = line.LotId, Quantity = line.Quantity,
                FromLocationId = line.FromLocationId, SuggestedLocationId = plan.LocationId,
                LicensePlateId = line.LicensePlateId, DecisionTraceId = plan.DecisionTraceId,
                StockReservationId = line.ReservationId, SourceReference = plan.Reference, IdempotencyKey = key }, cancellationToken);
            if (task.IsFailure) throw new InvalidOperationException(task.Error.Code);
            line.WorkTaskId = task.Value;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal async Task<decimal> IncomingAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var plans = await db.Plans.Include(plan => plan.Lines)
            .Where(plan => plan.LocationId == locationId && plan.Status == ReplenishmentPlanStatus.Open).ToListAsync(cancellationToken);
        var ids = plans.SelectMany(plan => plan.Lines).Where(line => line.WorkTaskId != null).Select(line => line.WorkTaskId!.Value).ToList();
        var states = await queue.FindManyAsync(ids, cancellationToken);
        decimal incoming = 0;
        foreach (var plan in plans)
        {
            var live = false;
            foreach (var line in plan.Lines.Where(line => !line.Refused))
            {
                var state = line.WorkTaskId is { } id ? states.GetValueOrDefault(id)?.State : null;
                if (state == WorkTaskState.Cancelled && line.ReservationId is { } reservationId)
                    await reservations.ReleaseAsync([reservationId], "Replenishment task cancelled", cancellationToken);
                if (state is null or WorkTaskState.Ready or WorkTaskState.Assigned)
                {
                    live = true;
                    if (line.ReservationId is not null) incoming += line.Quantity;
                }
            }
            if (!live) plan.Status = ReplenishmentPlanStatus.Finished;
        }
        return incoming;
    }
}
