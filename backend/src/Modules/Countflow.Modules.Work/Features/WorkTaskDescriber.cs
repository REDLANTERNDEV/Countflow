using Countflow.Modules.Work.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Work.Features;

/// <summary>
/// Turns stored task rows into something a person can read.
/// <para>
/// Four batch lookups across the module boundary rather than a join — Work cannot
/// join to <c>masterdata.Items</c> or <c>inventory.Lots</c> (ADR 0002). That is
/// the honest price of the boundary, and it is paid once per page rather than
/// once per row, which is the part that actually matters.
/// </para>
/// </summary>
public sealed class WorkTaskDescriber(
    IItemDirectory items,
    ILocationDirectory locations,
    IStockQuery stock)
{
    public async Task<IReadOnlyList<WorkTaskResponse>> DescribeAsync(
        IReadOnlyList<WorkTask> tasks,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var itemsById = await items.FindManyAsync(
            [.. tasks.Select(t => t.ItemId).Distinct()], cancellationToken);

        var locationIds = tasks
            .SelectMany(t => new[] { (Guid?)t.FromLocationId, t.SuggestedLocationId, t.CompletedAtLocationId })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var locationsById = await locations.FindManyAsync(locationIds, cancellationToken);

        var lotsById = await stock.FindLotsAsync(
            [.. tasks.Where(t => t.LotId is not null).Select(t => t.LotId!.Value).Distinct()],
            cancellationToken);

        var platesById = await stock.FindLicensePlateLabelsAsync(
            [.. tasks.Where(t => t.LicensePlateId is not null).Select(t => t.LicensePlateId!.Value).Distinct()],
            cancellationToken);

        return
        [
            .. tasks.Select(task =>
            {
                var item = itemsById.GetValueOrDefault(task.ItemId);
                var lot = task.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null;

                return new WorkTaskResponse(
                    task.Id,
                    task.Kind,
                    task.Status,
                    task.Priority,
                    item?.Sku ?? string.Empty,
                    item?.Description ?? string.Empty,
                    item?.BaseUom ?? string.Empty,
                    task.Quantity,
                    lot?.LotNumber,
                    lot?.ExpiryDate,
                    task.LicensePlateId is { } plateId ? platesById.GetValueOrDefault(plateId) : null,
                    locationsById.GetValueOrDefault(task.FromLocationId)?.Code ?? string.Empty,
                    Code(locationsById, task.SuggestedLocationId),
                    task.DecisionTraceId,
                    task.Condition,
                    task.SourceReference,
                    task.CreatedAt,
                    task.AssignedToUserName,
                    task.CompletedAt,
                    Code(locationsById, task.CompletedAtLocationId),
                    task.WasOverridden,
                    task.OverrideReason);
            })
        ];
    }

    public async Task<WorkTaskResponse> DescribeAsync(WorkTask task, CancellationToken cancellationToken) =>
        (await DescribeAsync([task], cancellationToken))[0];

    private static string? Code(IReadOnlyDictionary<Guid, LocationSnapshot> known, Guid? id) =>
        id is { } value ? known.GetValueOrDefault(value)?.Code : null;
}
