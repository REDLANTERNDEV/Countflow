using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published read surface for the past (ADR 0001, and the third place
/// the immutable ledger pays for itself).
/// <para>
/// <b>Why this is a fold and not a query.</b> <see cref="LedgerReplay"/> already
/// answers "what was here on 14 July" by summing the ledger up to a sequence, and
/// that is the right shape for one question asked by a person. Shadow replay asks
/// it once per historical decision — hundreds of times, at ascending instants — and
/// summing from the beginning each time is quadratic in history. So the movement
/// stream is walked exactly once, forwards, pausing at each checkpoint to hand out
/// what has accumulated so far. Linear in the ledger and linear in the number of
/// decisions, which is what makes replaying a quarter a thing somebody waits for
/// rather than abandons.
/// </para>
/// <para>
/// The projection is not read for the quantities. That is deliberate rather than
/// pedantic: <c>StockBalance</c> holds today's figure and has no memory, so the
/// only source for a past one is the ledger, and being forced to derive it here is
/// the same discipline the reconciler applies from the other direction.
/// </para>
/// </summary>
public sealed class StockHistory(InventoryDbContext dbContext, LedgerReplay replay) : IStockHistory
{
    public Task<long?> ResolveSequenceAsync(DateTimeOffset asOf, CancellationToken cancellationToken) =>
        replay.ResolveSequenceAsync(asOf, cancellationToken);

    public async IAsyncEnumerable<WarehouseAsOf> WalkAsync(
        IReadOnlyCollection<Guid> locationIds,
        IReadOnlyList<DateTimeOffset> checkpoints,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (checkpoints.Count == 0 || locationIds.Count == 0)
        {
            yield break;
        }

        var scope = locationIds.ToHashSet();
        var horizon = checkpoints[^1];

        var balanceIds = await ReadBalanceIdsAsync(scope, cancellationToken);
        var reservations = await ReadReservationsAsync(balanceIds.Values, horizon, cancellationToken);
        var counts = await ReadCountsAsync(scope, horizon, cancellationToken);

        var holdings = new Dictionary<HoldingKey, Cell>();

        // Open claims and the latest count per holding are both built incrementally
        // alongside the fold, because both are monotone in the checkpoint: a
        // reservation created before now was created before later, and a count that
        // is the latest as of now stays the latest until a newer one appears. Doing
        // either by filtering the whole list at every checkpoint would work and would
        // put the quadratic term back in by the side door.
        var open = new Dictionary<Guid, decimal>();
        var latestCount = new Dictionary<HoldingKey, HoldingCountHistory>();
        var reservationCursor = 0;
        var closingCursor = 0;
        var countCursor = 0;

        var closings = reservations
            .Where(r => r.ClosedAt is not null)
            .OrderBy(r => r.ClosedAt!.Value)
            .ToList();

        // Bounded at the last checkpoint rather than unbounded. Everything after it
        // is irrelevant to every snapshot this walk will produce, and on a ledger
        // with a year in it that bound is the difference between reading the window
        // and reading the archive.
        var stream = dbContext.StockMovements
            .AsNoTracking()
            .Where(m => m.OccurredAt <= horizon)
            .OrderBy(m => m.Sequence)
            .Select(m => new Posting(
                m.Sequence,
                m.OccurredAt,
                m.ItemId,
                m.LotId,
                m.Quantity,
                m.FromLocationId,
                m.FromLicensePlateId,
                m.FromStatus,
                m.ToLocationId,
                m.ToLicensePlateId,
                m.ToStatus,
                m.Type))
            .AsAsyncEnumerable()
            .GetAsyncEnumerator(cancellationToken);

        var sequence = 0L;
        var drained = false;
        var pending = (Posting?)null;

        try
        {
            foreach (var checkpoint in checkpoints)
            {
                // Fold everything at or before this instant. "At or before" and not
                // "before": a decision made in the same millisecond as the movement
                // that enabled it saw that movement, because the posting committed
                // first.
                while (!drained)
                {
                    if (pending is null)
                    {
                        if (!await stream.MoveNextAsync())
                        {
                            drained = true;
                            break;
                        }

                        pending = stream.Current;
                    }

                    if (pending.OccurredAt > checkpoint)
                    {
                        break;
                    }

                    Apply(holdings, pending);
                    sequence = pending.Sequence;
                    pending = null;
                }

                while (reservationCursor < reservations.Count
                       && reservations[reservationCursor].CreatedAt <= checkpoint)
                {
                    var reservation = reservations[reservationCursor++];
                    open[reservation.BalanceId] = open.GetValueOrDefault(reservation.BalanceId) + reservation.Quantity;
                }

                while (closingCursor < closings.Count && closings[closingCursor].ClosedAt!.Value <= checkpoint)
                {
                    var reservation = closings[closingCursor++];

                    // Only discharge what was counted in. A claim created and closed
                    // between two checkpoints is never added, and subtracting it
                    // would leave a holding reporting a negative commitment.
                    if (reservation.CreatedAt <= checkpoint)
                    {
                        var remaining = open.GetValueOrDefault(reservation.BalanceId) - reservation.Quantity;

                        if (remaining <= 0m)
                        {
                            open.Remove(reservation.BalanceId);
                        }
                        else
                        {
                            open[reservation.BalanceId] = remaining;
                        }
                    }
                }

                while (countCursor < counts.Count && counts[countCursor].CountedAt <= checkpoint)
                {
                    var count = counts[countCursor++];
                    latestCount[count.Key] = new HoldingCountHistory(count.CountedAt, count.AtMovementSequence);
                }

                yield return Snapshot(checkpoint, sequence, holdings, scope, balanceIds, open, latestCount);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    /// <summary>
    /// Double entry, folded. Each movement credits the holding it arrived at and
    /// debits the one it left, which is why the quantities here always sum to zero
    /// and why a reconstructed warehouse cannot quietly gain stock.
    /// </summary>
    private static void Apply(Dictionary<HoldingKey, Cell> holdings, Posting posting)
    {
        var sourceKey = new HoldingKey(posting.ItemId, posting.LotId, posting.FromLocationId, posting.FromLicensePlateId, posting.FromStatus);
        var incomingAge = ReceiptAge.Incoming(posting.Type, posting.OccurredAt, holdings.GetValueOrDefault(sourceKey).FirstReceivedAt);
        Fold(
            holdings,
            new HoldingKey(posting.ItemId, posting.LotId, posting.ToLocationId, posting.ToLicensePlateId, posting.ToStatus),
            posting.Quantity,
            posting,
            incomingAge);

        Fold(
            holdings,
            new HoldingKey(posting.ItemId, posting.LotId, posting.FromLocationId, posting.FromLicensePlateId, posting.FromStatus),
            -posting.Quantity,
            posting,
            null);
    }

    private static void Fold(
        Dictionary<HoldingKey, Cell> holdings,
        HoldingKey key,
        decimal delta,
        Posting posting,
        DateTimeOffset? incomingAge)
    {
        var current = holdings.GetValueOrDefault(key);

        holdings[key] = new Cell(current.Quantity + delta, posting.Sequence, posting.OccurredAt,
            ReceiptAge.After(current.Quantity, current.FirstReceivedAt, delta, incomingAge));
    }

    private static WarehouseAsOf Snapshot(
        DateTimeOffset checkpoint,
        long sequence,
        Dictionary<HoldingKey, Cell> holdings,
        HashSet<Guid> scope,
        IReadOnlyDictionary<HoldingKey, Guid> balanceIds,
        Dictionary<Guid, decimal> open,
        Dictionary<HoldingKey, HoldingCountHistory> latestCount)
    {
        var rows = new List<HistoricalHolding>();
        var counted = new Dictionary<Guid, HoldingCountHistory>();
        var reserved = new Dictionary<Guid, decimal>();

        foreach (var (key, cell) in holdings)
        {
            if (cell.Quantity == 0m || !scope.Contains(key.LocationId))
            {
                continue;
            }

            // A holding with no balance row cannot happen — rows are created on
            // first posting and never deleted — but a replay that threw here would
            // fail a whole run over one unresolvable candidate, and a candidate
            // nobody can name is better dropped than guessed at.
            if (!balanceIds.TryGetValue(key, out var balanceId))
            {
                continue;
            }

            rows.Add(new HistoricalHolding(
                balanceId,
                key.ItemId,
                key.LotId,
                key.LocationId,
                key.LicensePlateId,
                ToCondition(key.Status),
                cell.Quantity,
                cell.LastMovementSequence,
                cell.LastMovedAt) { FirstReceivedAt = cell.FirstReceivedAt });

            if (open.TryGetValue(balanceId, out var claim))
            {
                reserved[balanceId] = claim;
            }

            if (latestCount.TryGetValue(key, out var history))
            {
                counted[balanceId] = history;
            }
        }

        // Sorted, so a replay of the same window twice offers the rules the
        // candidates in the same order twice. The engine breaks ties on the
        // candidate key and is therefore already order-independent, but a diff that
        // shuffled its rows would be unreadable exactly where somebody is trying to
        // read it.
        rows.Sort(static (left, right) =>
        {
            var byLocation = left.LocationId.CompareTo(right.LocationId);

            return byLocation != 0 ? byLocation : left.BalanceId.CompareTo(right.BalanceId);
        });

        return new WarehouseAsOf(checkpoint, sequence == 0L ? null : sequence, rows, reserved, counted);
    }

    /// <summary>
    /// Every balance row at the locations in scope, keyed by the holding it stands
    /// for. Read once; balance rows are never deleted, so anything the fold turns up
    /// has one.
    /// </summary>
    private async Task<IReadOnlyDictionary<HoldingKey, Guid>> ReadBalanceIdsAsync(
        HashSet<Guid> scope,
        CancellationToken cancellationToken)
    {
        var ids = scope.ToArray();

        var rows = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => ids.Contains(b.LocationId))
            .OrderBy(b => b.LocationId)
            .ThenBy(b => b.ItemId)
            .Select(b => new BalanceRow(b.Id, b.ItemId, b.LotId, b.LocationId, b.LicensePlateId, b.Status))
            .ToListAsync(cancellationToken);

        var map = new Dictionary<HoldingKey, Guid>(rows.Count);

        foreach (var row in rows)
        {
            map[new HoldingKey(row.ItemId, row.LotId, row.LocationId, row.LicensePlateId, row.Status)] = row.Id;
        }

        return map;
    }

    private async Task<IReadOnlyList<ReservationRow>> ReadReservationsAsync(
        IEnumerable<Guid> balanceIds,
        DateTimeOffset horizon,
        CancellationToken cancellationToken)
    {
        var ids = balanceIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        return await dbContext.StockReservations
            .AsNoTracking()
            .Where(r => ids.Contains(r.BalanceId) && r.CreatedAt <= horizon)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new ReservationRow(r.BalanceId, r.Quantity, r.CreatedAt, r.ClosedAt))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<CountRow>> ReadCountsAsync(
        HashSet<Guid> scope,
        DateTimeOffset horizon,
        CancellationToken cancellationToken)
    {
        var ids = scope.ToArray();

        var rows = await dbContext.StockCounts
            .AsNoTracking()
            .Where(c => ids.Contains(c.LocationId) && c.CountedAt <= horizon)
            .OrderBy(c => c.CountedAt)
            .Select(c => new CountProjection(
                c.ItemId, c.LotId, c.LocationId, c.LicensePlateId, c.Status, c.CountedAt, c.AtMovementSequence))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new CountRow(
                new HoldingKey(row.ItemId, row.LotId, row.LocationId, row.LicensePlateId, row.Status),
                row.CountedAt,
                row.AtMovementSequence))
        ];
    }

    private static StockCondition ToCondition(InventoryStatus status) => status switch
    {
        InventoryStatus.Quarantine => StockCondition.Quarantine,
        InventoryStatus.Damaged => StockCondition.Damaged,
        InventoryStatus.OnHold => StockCondition.OnHold,
        InventoryStatus.Expired => StockCondition.Expired,
        _ => StockCondition.Available
    };

    /// <summary>The five parts of a holding, as a value that can key a dictionary.</summary>
    private readonly record struct HoldingKey(
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status);

    private readonly record struct Cell(decimal Quantity, long LastMovementSequence, DateTimeOffset LastMovedAt, DateTimeOffset? FirstReceivedAt);

    private sealed record Posting(
        long Sequence,
        DateTimeOffset OccurredAt,
        Guid ItemId,
        Guid? LotId,
        decimal Quantity,
        Guid FromLocationId,
        Guid? FromLicensePlateId,
        InventoryStatus FromStatus,
        Guid ToLocationId,
        Guid? ToLicensePlateId,
        InventoryStatus ToStatus,
        MovementType Type);

    private sealed record BalanceRow(
        Guid Id,
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status);

    private sealed record ReservationRow(
        Guid BalanceId,
        decimal Quantity,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ClosedAt);

    private sealed record CountProjection(
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status,
        DateTimeOffset CountedAt,
        long AtMovementSequence);

    private sealed record CountRow(HoldingKey Key, DateTimeOffset CountedAt, long AtMovementSequence);
}
