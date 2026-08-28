using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <summary>
/// Re-derives the two running totals a return moves, and derives the return's own
/// status from its lines.
/// <para>
/// Every number here is a <c>SUM</c> over append-only rows, never an increment, and
/// that is the fourth time this system has made the argument (ADR 0001, one floor
/// down): a total incremented in place cannot be made idempotent, and a retried
/// call that added its quantity twice would close a line with goods still standing
/// in the bay, or credit an order with more coming back than ever left.
/// </para>
/// <para>
/// Status is derived for the same reason <c>Asn.Status</c> is. A return is finished
/// when every line has been decided in full, which is a fact about the lines; a
/// third state somebody presses a button for would be a status that agrees with
/// them right up until the day it does not.
/// </para>
/// </summary>
internal static class ReturnProjection
{
    /// <summary>
    /// Recomputes one line's dispositioned total and the return's status, from the
    /// rows as they now stand.
    /// </summary>
    internal static async Task RefreshAsync(
        OutboundDbContext dbContext,
        Return returned,
        ReturnLine line,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        line.DispositionedQuantity = await dbContext.ReturnLineDispositions
            .Where(d => d.ReturnLineId == line.Id)
            .SumAsync(d => d.Quantity, cancellationToken);

        returned.Status = returned.Lines.All(l => l.DispositionedQuantity >= l.Quantity)
            ? ReturnStatus.Dispositioned
            : ReturnStatus.Received;

        if (returned.Status == ReturnStatus.Dispositioned)
        {
            returned.ClosedAt ??= now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await RefreshOrderLinesAsync(dbContext, returned, cancellationToken);
    }

    /// <summary>
    /// Recomputes <see cref="SalesOrderLine.ReturnedQuantity"/> for every order line
    /// this return touches.
    /// <para>
    /// Summed over <b>return lines</b> rather than over dispositions, deliberately.
    /// What the order wants to know is how much came back, which is settled the
    /// moment the goods are on the ledger; whether the warehouse has since decided
    /// to restock or destroy them is a different question and belongs to the return.
    /// Counting dispositions here would leave an order claiming nothing had come back
    /// while the pallet sat in the returns bay waiting for somebody to look at it.
    /// </para>
    /// <para>
    /// Not capped at the ordered quantity, and no check constraint enforces one. A
    /// customer returning more than the order says is the mirror of an
    /// over-delivery — usually a consolidated shipment, occasionally a mistake — and
    /// both are facts worth recording rather than writes worth refusing.
    /// </para>
    /// </summary>
    internal static async Task RefreshOrderLinesAsync(
        OutboundDbContext dbContext,
        Return returned,
        CancellationToken cancellationToken)
    {
        var orderLineIds = returned.Lines
            .Where(l => l.SalesOrderLineId is not null)
            .Select(l => l.SalesOrderLineId!.Value)
            .Distinct()
            .ToList();

        if (orderLineIds.Count == 0)
        {
            return;
        }

        var totals = await dbContext.ReturnLines
            .Where(l => l.SalesOrderLineId != null && orderLineIds.Contains(l.SalesOrderLineId!.Value))
            .GroupBy(l => l.SalesOrderLineId!.Value)
            .Select(group => new { SalesOrderLineId = group.Key, Quantity = group.Sum(l => l.Quantity) })
            .ToListAsync(cancellationToken);

        var orderLines = await dbContext.SalesOrderLines
            .Where(l => orderLineIds.Contains(l.Id))
            .ToListAsync(cancellationToken);

        foreach (var orderLine in orderLines)
        {
            orderLine.ReturnedQuantity = totals
                .SingleOrDefault(t => t.SalesOrderLineId == orderLine.Id)?.Quantity ?? 0m;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
