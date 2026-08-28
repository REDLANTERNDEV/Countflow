using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <summary>
/// Turns stored returns into something a person can read, and asks the work queue
/// what became of the inspections.
/// <para>
/// The same shape as <c>OrderDescriber</c> and for the same reasons: two batch
/// lookups across module boundaries rather than joins (ADR 0002), and the
/// inspection's state asked for on every read rather than mirrored into a column
/// here that would eventually disagree with the queue.
/// </para>
/// </summary>
public sealed class ReturnDescriber(
    IItemDirectory items,
    ILocationDirectory locations,
    IWorkQueue work)
{
    public async Task<IReadOnlyList<ReturnResponse>> DescribeAsync(
        IReadOnlyList<Return> returns,
        CancellationToken cancellationToken)
    {
        if (returns.Count == 0)
        {
            return [];
        }

        var lines = returns.SelectMany(r => r.Lines).ToList();

        var itemsById = await items.FindManyAsync(
            [.. lines.Select(l => l.ItemId).Distinct()], cancellationToken);

        var locationsById = await locations.FindManyAsync(
            [.. returns.Select(r => r.ReceivedAtLocationId).Distinct()], cancellationToken);

        var tasksById = await work.FindManyAsync(
            [.. lines.Where(l => l.InspectionTaskId is not null).Select(l => l.InspectionTaskId!.Value)],
            cancellationToken);

        return
        [
            .. returns.Select(returned => new ReturnResponse(
                returned.Id,
                returned.Reference,
                returned.Status,
                returned.SalesOrderReference,
                returned.SalesOrderId,
                returned.CustomerName,
                locationsById.GetValueOrDefault(returned.ReceivedAtLocationId)?.Code ?? string.Empty,
                returned.CreatedAt,
                returned.CreatedByUserName,
                returned.ClosedAt,
                returned.Notes,
                [
                    .. returned.Lines
                        .OrderBy(line => line.LineNumber)
                        .Select(line => Describe(line, itemsById, tasksById))
                ]))
        ];
    }

    public async Task<ReturnResponse> DescribeAsync(Return returned, CancellationToken cancellationToken) =>
        (await DescribeAsync([returned], cancellationToken))[0];

    private static ReturnLineResponse Describe(
        ReturnLine line,
        IReadOnlyDictionary<Guid, ItemSnapshot> itemsById,
        IReadOnlyDictionary<Guid, WorkTaskSummary> tasksById)
    {
        var item = itemsById.GetValueOrDefault(line.ItemId);

        var inspection = line.InspectionTaskId is { } taskId
            ? tasksById.GetValueOrDefault(taskId)
            : null;

        return new ReturnLineResponse(
            line.Id,
            line.LineNumber,
            item?.Sku ?? string.Empty,
            item?.Description ?? string.Empty,
            item?.BaseUom ?? string.Empty,
            line.Quantity,
            line.DispositionedQuantity,
            line.OutstandingQuantity,
            line.LotNumber,
            line.ExpiryDate,
            line.SalesOrderLineId,
            line.InspectionTaskId,
            inspection?.State,
            [
                .. line.Dispositions
                    .OrderBy(d => d.DecidedAt)
                    .Select(d => new ReturnDispositionResponse(
                        d.Id,
                        d.Disposition,
                        d.Quantity,
                        d.DecisionTraceId,
                        d.WasOverridden,
                        d.MovementId,
                        d.WorkTaskId,
                        d.DecidedAt,
                        d.DecidedByUserName,
                        d.Notes))
            ]);
    }
}
