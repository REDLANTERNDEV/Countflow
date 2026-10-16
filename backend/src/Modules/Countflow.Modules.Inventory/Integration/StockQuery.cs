using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published read surface for slotting decisions (ADR 0002).
/// <para>
/// Reads the projection rather than replaying the ledger. A putaway decision is
/// about where stock is <i>now</i>, and the projection is written in the same
/// transaction as the movement that changed it — so "now" is exactly what it
/// holds. Point-in-time questions go through <c>LedgerReplay</c>, and they are a
/// different question.
/// </para>
/// </summary>
public sealed class StockQuery(InventoryDbContext dbContext, IItemDirectory items) : IStockQuery
{
    public async Task<IReadOnlyDictionary<Guid, LocationOccupancy>> OccupancyAsync(
        IReadOnlyCollection<Guid> locationIds,
        Guid subjectItemId,
        Guid? subjectLotId,
        CancellationToken cancellationToken)
    {
        var subject = new StockSubject(subjectItemId, subjectLotId);
        var batch = await OccupancyAsync(locationIds, [subject], cancellationToken);
        return batch[subject];
    }

    public async Task<IReadOnlyDictionary<StockSubject, IReadOnlyDictionary<Guid, LocationOccupancy>>> OccupancyAsync(
        IReadOnlyCollection<Guid> locationIds,
        IReadOnlyCollection<StockSubject> subjects,
        CancellationToken cancellationToken)
    {
        var wanted = subjects.Distinct().ToArray();
        if (locationIds.Count == 0 || wanted.Length == 0)
        {
            return wanted.ToDictionary(subject => subject,
                _ => (IReadOnlyDictionary<Guid, LocationOccupancy>)new Dictionary<Guid, LocationOccupancy>());
        }

        var ids = locationIds.Distinct().ToArray();

        // Every status, not just Available. Occupancy here is physical: a pallet
        // in quarantine takes up exactly as much of the bay as a good one, and a
        // capacity rule that ignored it would send a truck to a full slot.
        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => ids.Contains(b.LocationId) && b.Quantity > 0)
            .Select(b => new OccupancyLine(b.LocationId, b.ItemId, b.LotId, b.Quantity))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return wanted.ToDictionary(subject => subject,
                _ => (IReadOnlyDictionary<Guid, LocationOccupancy>)new Dictionary<Guid, LocationOccupancy>());
        }

        // One batch lookup across the module boundary rather than a join —
        // inventory cannot join to masterdata.Items (ADR 0002). Distinct items
        // actually in stock is a far smaller set than the catalogue, so this stays
        // cheap even at full warehouse scale.
        var itemsById = await items.FindManyAsync(
            [.. rows.Select(r => r.ItemId).Distinct()], cancellationToken);

        // The fold itself lives on the contract, so that shadow replay describing
        // this bay as it stood in March describes it in exactly these terms.
        return wanted.ToDictionary(subject => subject,
            subject => LocationOccupancy.Summarise(rows, itemsById, subject.ItemId, subject.LotId));
    }

    public async Task<IReadOnlyList<PlateLine>> PlateContentsAsync(
        Guid licensePlateId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => b.LicensePlateId == licensePlateId
                        && b.LocationId == locationId
                        && b.Quantity > 0)
            .OrderBy(b => b.ItemId)
            .ThenBy(b => b.LotId)
            .ThenBy(b => b.Status)
            .Select(b => new { b.ItemId, b.LotId, b.Status, b.Quantity })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new PlateLine(r.ItemId, r.LotId, ToCondition(r.Status), r.Quantity))];
    }

    /// <summary>
    /// Everything on a plate, wherever it is. The location comes back on each line
    /// because a plate has no location of its own — see <c>LicensePlate</c>, which
    /// refuses a <c>CurrentLocationId</c> for exactly this reason (ADR 0001).
    /// </summary>
    public async Task<IReadOnlyList<PlateHolding>> PlateHoldingsAsync(
        Guid licensePlateId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => b.LicensePlateId == licensePlateId && b.Quantity > 0)
            .OrderBy(b => b.ItemId)
            .ThenBy(b => b.LotId)
            .ThenBy(b => b.Status)
            .Select(b => new { b.ItemId, b.LotId, b.LocationId, b.Status, b.Quantity })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => new PlateHolding(
                r.ItemId,
                r.LotId,
                r.LocationId,
                ToCondition(r.Status),
                r.Quantity))
        ];
    }

    public async Task<Guid?> FindLicensePlateAsync(string sscc, CancellationToken cancellationToken)
    {
        var normalised = sscc.Trim();

        return await dbContext.LicensePlates
            .AsNoTracking()
            .Where(p => p.Sscc == normalised)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, LotSnapshot>> FindLotsAsync(
        IReadOnlyCollection<Guid> lotIds,
        CancellationToken cancellationToken)
    {
        if (lotIds.Count == 0)
        {
            return new Dictionary<Guid, LotSnapshot>();
        }

        var ids = lotIds.Distinct().ToArray();

        return await dbContext.Lots
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new LotSnapshot(l.Id, l.ItemId, l.LotNumber, l.ExpiryDate))
            .ToDictionaryAsync(l => l.Id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindLicensePlateLabelsAsync(
        IReadOnlyCollection<Guid> licensePlateIds,
        CancellationToken cancellationToken)
    {
        if (licensePlateIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = licensePlateIds.Distinct().ToArray();

        return await dbContext.LicensePlates
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sscc, cancellationToken);
    }

    private static StockCondition ToCondition(InventoryStatus status) => status switch
    {
        InventoryStatus.Quarantine => StockCondition.Quarantine,
        InventoryStatus.Damaged => StockCondition.Damaged,
        InventoryStatus.OnHold => StockCondition.OnHold,
        InventoryStatus.Expired => StockCondition.Expired,
        _ => StockCondition.Available
    };
}
