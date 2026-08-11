using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published read surface for "what can I promise?" (ADR 0002).
/// <para>
/// The mirror of <see cref="StockQuery"/>. That one answers where stock could go
/// and is shaped around a subject item looking for a home; this one answers which
/// stock could satisfy a demand, and is shaped around holdings and what is left
/// of them after everybody else's claims.
/// </para>
/// </summary>
public sealed class StockAvailability(
    InventoryDbContext dbContext,
    ReservationLedger reservations,
    ILocationDirectory locations) : IStockAvailability
{
    public async Task<IReadOnlyList<StockHolding>> HoldingsAsync(
        Guid warehouseId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        // Filtered by item before anything else, because that is the small set.
        // Filtering by warehouse first would mean naming two thousand locations in
        // an IN list to find the four rows that hold the SKU — the balance rows
        // carry no warehouse of their own, since a location's warehouse is
        // MasterData's fact and not one this module may copy.
        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => b.ItemId == itemId && b.Quantity > 0)
            .Select(b => new Row(b.Id, b.ItemId, b.LotId, b.LocationId, b.LicensePlateId, b.Status, b.Quantity))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var locationsById = await locations.FindManyAsync(
            [.. rows.Select(r => r.LocationId).Distinct()], cancellationToken);

        // Virtual counterparties are dropped rather than offered and excluded. The
        // Supplier account holds a large negative balance by design and is not a
        // place anybody could pick from, so listing it as a rejected candidate
        // would pad every trace with the same meaningless line.
        var held = rows
            .Where(r => locationsById.TryGetValue(r.LocationId, out var location)
                        && location.WarehouseId == warehouseId
                        && !location.IsVirtual)
            .ToList();

        if (held.Count == 0)
        {
            return [];
        }

        var reserved = await reservations.OpenByBalanceAsync(
            [.. held.Select(r => r.BalanceId)], cancellationToken);

        var lotsById = await dbContext.Lots
            .AsNoTracking()
            .Where(l => l.ItemId == itemId)
            .ToDictionaryAsync(l => l.Id, cancellationToken);

        var platesById = await dbContext.LicensePlates
            .AsNoTracking()
            .Where(p => held.Select(r => r.LicensePlateId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sscc, cancellationToken);

        return
        [
            .. held
                // Deterministic, so two evaluations of an unchanged warehouse hand
                // the engine its candidates in the same order — which is what makes
                // a tie break the same way twice.
                .OrderBy(r => r.LocationId)
                .ThenBy(r => r.LotId)
                .ThenBy(r => r.Status)
                .Select(r =>
                {
                    var lot = r.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null;

                    return new StockHolding(
                        r.BalanceId,
                        r.ItemId,
                        r.LotId,
                        lot?.LotNumber,
                        lot?.ExpiryDate,
                        r.LocationId,
                        r.LicensePlateId,
                        r.LicensePlateId is { } plateId ? platesById.GetValueOrDefault(plateId) : null,
                        ToCondition(r.Status),
                        r.Quantity,
                        reserved.GetValueOrDefault(r.BalanceId))
                    {
                        LocationCode = locationsById[r.LocationId].Code
                    };
                })
        ];
    }

    public Task<IReadOnlyDictionary<Guid, decimal>> ReservedByBalanceAsync(
        IReadOnlyCollection<Guid> balanceIds,
        CancellationToken cancellationToken) =>
        reservations.OpenByBalanceAsync(balanceIds, cancellationToken);

    private sealed record Row(
        Guid BalanceId,
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status,
        decimal Quantity);

    private static StockCondition ToCondition(InventoryStatus status) => status switch
    {
        InventoryStatus.Quarantine => StockCondition.Quarantine,
        InventoryStatus.Damaged => StockCondition.Damaged,
        InventoryStatus.OnHold => StockCondition.OnHold,
        InventoryStatus.Expired => StockCondition.Expired,
        _ => StockCondition.Available
    };
}
