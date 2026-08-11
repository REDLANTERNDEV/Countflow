using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published read surface for counting (ADR 0002) — every holding the
/// warehouse could be asked to verify, with the history that decides whether it is
/// worth asking.
/// <para>
/// Deliberately unfiltered beyond dropping the counterparty accounts, which are
/// not places anybody can stand in front of. It is tempting to return only the
/// holdings that are due — but a candidate excluded by a query never appears in the
/// decision trace, and "why has that bay not been counted since March" is exactly
/// the question the accuracy dashboard exists to make answerable (ADR 0004). The
/// rule set does the excluding, and says so.
/// </para>
/// </summary>
public sealed class StockCounts(
    InventoryDbContext dbContext,
    ILocationDirectory locations) : IStockCounts
{
    public async Task<IReadOnlyList<CountCandidate>> CandidatesAsync(
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        // The warehouse is MasterData's fact, not this module's, so the balances are
        // narrowed by the location ids the directory hands over rather than by a
        // join this schema cannot make. Storage candidates are already the physical
        // ones — a virtual counterparty is not among them, which is the filter we
        // want and the only one we want.
        var physical = await locations.FindStorageCandidatesAsync(warehouseId, cancellationToken);
        if (physical.Count == 0)
        {
            return [];
        }

        var byId = physical.ToDictionary(location => location.Id);
        var ids = byId.Keys.ToArray();

        // Filtered and ordered before the projection, in the module directories'
        // idiom and for the same reason: EF cannot translate a comparison against a
        // constructor call, and it fails at runtime rather than at build time.
        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => ids.Contains(b.LocationId) && b.Quantity != 0m)
            .OrderBy(b => b.LocationId)
            .ThenBy(b => b.ItemId)
            .Select(b => new Row(
                b.Id,
                b.ItemId,
                b.LotId,
                b.LocationId,
                b.LicensePlateId,
                b.Status,
                b.Quantity,
                b.LastMovementSequence,
                b.UpdatedAt))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var lotIds = rows
            .Where(r => r.LotId is not null)
            .Select(r => r.LotId!.Value)
            .Distinct()
            .ToArray();

        var lots = await dbContext.Lots
            .AsNoTracking()
            .Where(l => lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.LotNumber, l.ExpiryDate })
            .ToDictionaryAsync(l => l.Id, cancellationToken);

        var history = await ReadCountHistoryAsync(rows, cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                var lot = row.LotId is { } lotId ? lots.GetValueOrDefault(lotId) : null;
                var counted = history.GetValueOrDefault(HistoryKey.Of(row));

                return new CountCandidate(
                    row.Id,
                    row.ItemId,
                    row.LotId,
                    lot?.LotNumber,
                    lot?.ExpiryDate,
                    row.LocationId,
                    row.LicensePlateId,
                    ToCondition(row.Status),
                    row.Quantity,
                    counted?.CountedAt,
                    row.LastMovementSequence,
                    counted?.AtMovementSequence,
                    row.UpdatedAt)
                {
                    LocationCode = byId[row.LocationId].Code
                };
            })
        ];
    }

    /// <summary>
    /// The most recent count of each candidate holding.
    /// <para>
    /// One grouped pass over the counts rather than a lookup per candidate. A count
    /// row names the five parts of a holding rather than a balance id — see
    /// <see cref="StockCount"/> for why, and it is the same reason this cannot be a
    /// join on <c>BalanceId</c>: a count that found stock in a bay the system
    /// thought was empty has no balance row to have named.
    /// </para>
    /// <para>
    /// Bounded to the locations in question and then folded in memory. The
    /// alternative is a lateral join per holding, and a warehouse counts a few
    /// thousand times a year against a ledger that grows by that much in a week —
    /// the count table is the small one here, and it is worth remembering which of
    /// the two that is.
    /// </para>
    /// </summary>
    private async Task<Dictionary<HistoryKey, CountHistory>> ReadCountHistoryAsync(
        IReadOnlyList<Row> rows,
        CancellationToken cancellationToken)
    {
        var locationIds = rows.Select(r => r.LocationId).Distinct().ToArray();

        var counts = await dbContext.StockCounts
            .AsNoTracking()
            .Where(c => locationIds.Contains(c.LocationId))
            .OrderBy(c => c.CountedAt)
            .Select(c => new CountRow(
                c.ItemId, c.LotId, c.LocationId, c.LicensePlateId, c.Status,
                c.CountedAt, c.AtMovementSequence))
            .ToListAsync(cancellationToken);

        var history = new Dictionary<HistoryKey, CountHistory>();

        // Ascending, so the last write per key wins and is therefore the latest.
        foreach (var count in counts)
        {
            history[HistoryKey.Of(count)] = new CountHistory(count.CountedAt, count.AtMovementSequence);
        }

        return history;
    }

    private static StockCondition ToCondition(InventoryStatus status) => status switch
    {
        InventoryStatus.Quarantine => StockCondition.Quarantine,
        InventoryStatus.Damaged => StockCondition.Damaged,
        InventoryStatus.OnHold => StockCondition.OnHold,
        InventoryStatus.Expired => StockCondition.Expired,
        _ => StockCondition.Available
    };

    private sealed record Row(
        Guid Id,
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status,
        decimal Quantity,
        long LastMovementSequence,
        DateTimeOffset UpdatedAt);

    private sealed record CountRow(
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status,
        DateTimeOffset CountedAt,
        long AtMovementSequence);

    private sealed record CountHistory(DateTimeOffset CountedAt, long AtMovementSequence);

    /// <summary>The five parts of a holding, as a value that can key a dictionary.</summary>
    private readonly record struct HistoryKey(
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status)
    {
        public static HistoryKey Of(Row row) =>
            new(row.ItemId, row.LotId, row.LocationId, row.LicensePlateId, row.Status);

        public static HistoryKey Of(CountRow row) =>
            new(row.ItemId, row.LotId, row.LocationId, row.LicensePlateId, row.Status);
    }
}
