using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// Turns stored orders into something a person can read, and asks the work queue
/// what became of the picks.
/// <para>
/// Three batch lookups across module boundaries rather than joins — Outbound
/// cannot join to <c>masterdata.Items</c>, <c>masterdata.Locations</c> or
/// <c>work.WorkTasks</c> (ADR 0002). The third one is the interesting one: pick
/// progress is not stored here at all. Asking the queue on every read costs one
/// query per page and means this module can never be wrong about how far an order
/// has got, which a mirrored column would eventually be.
/// </para>
/// </summary>
public sealed class OrderDescriber(
    IItemDirectory items,
    ILocationDirectory locations,
    IWorkQueue work)
{
    public async Task<IReadOnlyList<SalesOrderResponse>> DescribeAsync(
        IReadOnlyList<SalesOrder> orders,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var lines = orders.SelectMany(o => o.Lines).ToList();
        var allocations = lines.SelectMany(l => l.Allocations).ToList();

        var itemsById = await items.FindManyAsync(
            [.. lines.Select(l => l.ItemId).Distinct()], cancellationToken);

        var locationIds = orders
            .Where(o => o.StagingLocationId is not null)
            .Select(o => o.StagingLocationId!.Value)
            .Concat(allocations.Select(a => a.LocationId))
            .Distinct()
            .ToList();

        var tasksById = await work.FindManyAsync(
            [.. allocations.Where(a => a.WorkTaskId is not null).Select(a => a.WorkTaskId!.Value)],
            cancellationToken);

        // Where the pickers actually dropped, which may not be the bench the order
        // named — an override is recorded rather than refused, and the despatch
        // has to leave from wherever the goods really are.
        var completedAt = tasksById.Values
            .Where(t => t.CompletedAtLocationId is not null)
            .Select(t => t.CompletedAtLocationId!.Value);

        var locationsById = await locations.FindManyAsync(
            [.. locationIds.Concat(completedAt).Distinct()], cancellationToken);

        return
        [
            .. orders.Select(order => new SalesOrderResponse(
                order.Id,
                order.Reference,
                order.CustomerName,
                order.Status,
                order.StagingLocationId is { } staging
                    ? locationsById.GetValueOrDefault(staging)?.Code
                    : null,
                order.ShipBy,
                order.Priority,
                order.WaveId,
                order.Notes,
                order.CreatedAt,
                order.ReleasedAt,
                order.PackedAt,
                order.DespatchedAt,
                [
                    .. order.Lines
                        .OrderBy(line => line.LineNumber)
                        .Select(line => Describe(line, itemsById, locationsById, tasksById))
                ]))
        ];
    }

    public async Task<SalesOrderResponse> DescribeAsync(SalesOrder order, CancellationToken cancellationToken) =>
        (await DescribeAsync([order], cancellationToken))[0];

    private static OrderLineResponse Describe(
        SalesOrderLine line,
        IReadOnlyDictionary<Guid, ItemSnapshot> itemsById,
        IReadOnlyDictionary<Guid, LocationSnapshot> locationsById,
        IReadOnlyDictionary<Guid, WorkTaskSummary> tasksById)
    {
        var item = itemsById.GetValueOrDefault(line.ItemId);

        var allocations = line.Allocations
            .OrderBy(a => a.AllocatedAt)
            .Select(allocation =>
            {
                var task = allocation.WorkTaskId is { } taskId
                    ? tasksById.GetValueOrDefault(taskId)
                    : null;

                var picked = task?.State == WorkTaskState.Completed;

                return new OrderAllocationResponse(
                    allocation.Id,
                    allocation.Quantity,
                    allocation.LocationCode,
                    allocation.LotNumber,
                    allocation.ExpiryDate,
                    allocation.DecisionTraceId,
                    allocation.WorkTaskId,
                    task?.State,
                    picked ? allocation.Quantity : 0m,
                    task?.CompletedAtLocationId is { } dropped
                        ? locationsById.GetValueOrDefault(dropped)?.Code
                        : null);
            })
            .ToList();

        return new OrderLineResponse(
            line.Id,
            line.LineNumber,
            item?.Sku ?? string.Empty,
            item?.Description ?? string.Empty,
            item?.BaseUom ?? string.Empty,
            line.OrderedQuantity,
            line.AllocatedQuantity,
            line.OutstandingQuantity,
            allocations.Sum(a => a.PickedQuantity),
            line.ReturnedQuantity,
            // The batch number is Inventory's to know; the order carries the id it
            // was created with, and the console shows it against the allocations
            // where the lot is already resolved.
            null,
            allocations);
    }
}
