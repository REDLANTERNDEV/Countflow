using Countflow.Modules.Replenishment.Domain;
using Countflow.Modules.Replenishment.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Replenishment.Features;

public sealed class ReplenishmentDescriber(ReplenishmentDbContext db, ILocationDirectory locations,
    IItemDirectory items, IWarehouseDirectory warehouses, IStockAvailability availability, IWorkQueue queue)
{
    public async Task<IReadOnlyList<PickFacePolicyResponse>> PoliciesAsync(IReadOnlyList<PickFacePolicy> policies, CancellationToken cancellationToken)
    {
        var locationIds = policies.Select(policy => policy.LocationId).ToList();
        var plans = await db.Plans.AsNoTracking().Include(plan => plan.Lines)
            .Where(plan => locationIds.Contains(plan.LocationId) && plan.Status == ReplenishmentPlanStatus.Open).ToListAsync(cancellationToken);
        var tasks = await queue.FindManyAsync(plans.SelectMany(plan => plan.Lines).Where(line => line.WorkTaskId != null)
            .Select(line => line.WorkTaskId!.Value).ToList(), cancellationToken);
        var incoming = plans.GroupBy(plan => plan.LocationId).ToDictionary(group => group.Key, group => group.SelectMany(plan => plan.Lines)
            .Where(line => !line.Refused && line.ReservationId != null && (line.WorkTaskId is not { } id
                || tasks.GetValueOrDefault(id)?.State is null or WorkTaskState.Ready or WorkTaskState.Assigned)).Sum(line => line.Quantity));
        var locationNames = await locations.FindManyAsync(locationIds, cancellationToken);
        var itemNames = await items.FindManyAsync(policies.Select(policy => policy.ItemId).Distinct().ToList(), cancellationToken);
        var warehouseNames = (await warehouses.ListAsync(cancellationToken)).ToDictionary(warehouse => warehouse.Id);
        var holdings = new Dictionary<(Guid WarehouseId, Guid ItemId), IReadOnlyList<StockHolding>>();
        foreach (var key in policies.Select(policy => (policy.WarehouseId, policy.ItemId)).Distinct())
            holdings[key] = await availability.HoldingsAsync(key.WarehouseId, key.ItemId, cancellationToken);
        return policies.Select(policy =>
        {
            var stock = holdings[(policy.WarehouseId, policy.ItemId)].Where(holding => holding.LocationId == policy.LocationId).ToList();
            return new PickFacePolicyResponse(policy.LocationId, locationNames.GetValueOrDefault(policy.LocationId)?.Code ?? "?",
                warehouseNames.GetValueOrDefault(policy.WarehouseId)?.Code ?? "?", itemNames.GetValueOrDefault(policy.ItemId)?.Sku ?? "?",
                itemNames.GetValueOrDefault(policy.ItemId)?.BaseUom ?? "", policy.Minimum, policy.Maximum, policy.Enabled,
                stock.Sum(holding => holding.OnHandQuantity), stock.Where(holding => holding.Condition == StockCondition.Available).Sum(holding => holding.AvailableQuantity),
                incoming.GetValueOrDefault(policy.LocationId), policy.LastCheckedAt, policy.LastShortfall, policy.PendingPlanId,
                policy.LastPlanId, policy.LastDecisionTraceId, policy.LastErrorCode);
        }).ToList();
    }

    public async Task<ReplenishmentPlanResponse> PlanAsync(ReplenishmentPlan plan, CancellationToken cancellationToken)
    {
        var locationNames = await locations.FindManyAsync([plan.LocationId, .. plan.Lines.Select(line => line.FromLocationId)], cancellationToken);
        var item = await items.FindAsync(plan.ItemId, cancellationToken);
        var tasks = await queue.FindManyAsync(plan.Lines.Where(line => line.WorkTaskId != null).Select(line => line.WorkTaskId!.Value).ToList(), cancellationToken);
        return new ReplenishmentPlanResponse(plan.Id, plan.Reference, plan.Status, locationNames.GetValueOrDefault(plan.LocationId)?.Code ?? "?",
            item?.Sku ?? "?", plan.RequestedQuantity, plan.DecisionTraceId, plan.CreatedAt,
            plan.Lines.Select(line => new ReplenishmentLineResponse(line.Id, locationNames.GetValueOrDefault(line.FromLocationId)?.Code ?? "?",
                line.Quantity, line.ReservationId, line.WorkTaskId, line.WorkTaskId is { } id ? tasks.GetValueOrDefault(id)?.State : null,
                line.Refused, line.ErrorCode)).ToList());
    }
}
