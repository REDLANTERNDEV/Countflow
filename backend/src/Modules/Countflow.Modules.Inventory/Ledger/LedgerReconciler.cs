using System.Data;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>One holding where the projection and the ledger disagree.</summary>
public sealed record BalanceDrift(
    Guid ItemId,
    Guid? LotId,
    Guid LocationId,
    Guid? LicensePlateId,
    InventoryStatus Status,
    decimal Projected,
    decimal Replayed,
    long LastMovementSequence)
{
    public decimal Difference => Projected - Replayed;
}

/// <summary>
/// The outcome of a reconciliation pass.
/// </summary>
/// <param name="LedgerSum">
/// The sum of every signed posting in the ledger. Must be exactly zero: stock is
/// only ever moved, never created or destroyed, so anything else means an entry
/// was written with sides that do not balance — a far deeper problem than drift.
/// </param>
/// <param name="Elapsed">
/// How long the pass took. Worth keeping rather than merely logging: replaying the
/// whole ledger is the one operation whose cost grows with history rather than with
/// the size of the warehouse, so this is the series that will say when incremental
/// reconciliation stops being premature.
/// </param>
public sealed record ReconciliationReport(
    DateTimeOffset RunAt,
    long MovementsReplayed,
    int HoldingsChecked,
    decimal LedgerSum,
    TimeSpan Elapsed,
    IReadOnlyList<BalanceDrift> Drift)
{
    public bool IsClean => Drift.Count == 0 && LedgerSum == 0m;

    /// <summary>Largest absolute disagreement found, in base units. Zero on a clean pass.</summary>
    public decimal WorstDrift =>
        Drift.Count == 0 ? 0m : Drift.Max(d => Math.Abs(d.Difference));
}

/// <summary>
/// Replays the ledger and compares it with the stored balances.
/// <para>
/// This is what makes ADR 0001 a claim the system can check rather than a design
/// intention. The projection is written in the same transaction as its movement,
/// so it should never diverge — but "should never" is exactly the kind of
/// statement that quietly stops being true, and the whole product thesis is that
/// Countflow can prove its numbers. A reconciler that finds nothing for a year is
/// doing its job.
/// </para>
/// <para>
/// Read-only by design: it reports drift, it does not repair it. Silently
/// rewriting a balance would destroy the evidence needed to work out which code
/// path caused it, and the correct repair is a compensating entry posted by
/// someone who understands what happened.
/// </para>
/// </summary>
public sealed class LedgerReconciler(
    InventoryDbContext dbContext,
    LedgerReplay replay,
    TimeProvider clock,
    ILogger<LedgerReconciler> logger)
{
    public async Task<ReconciliationReport> RunAsync(CancellationToken cancellationToken)
    {
        // See StockLedger: the retrying execution strategy will not allow a
        // hand-rolled transaction unless the whole thing is handed to it as one
        // retriable unit. A reconciliation pass is pure reads, so replaying it is
        // free of side effects.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(ReadAndCompareAsync, cancellationToken);
    }

    private async Task<ReconciliationReport> ReadAndCompareAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // One snapshot for all three reads, and the only reason this method needs a
        // transaction at all. Read committed would let a movement commit between the
        // replay and the projection read, so the ledger would be summed one
        // statement later than the balances were sampled — and the difference would
        // be reported as drift. An alarm that cries wolf whenever the warehouse is
        // busy is an alarm that gets switched off.
        await using var snapshot = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);

        var replayed = await replay.ReplayAsync(asOfSequence: null, cancellationToken);

        var projected = await dbContext.StockBalances
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var movementCount = await dbContext.StockMovements.LongCountAsync(cancellationToken);

        var byKey = replayed.ToDictionary(
            r => (r.ItemId, r.LotId, r.LocationId, r.LicensePlateId, r.Status));

        var drift = new List<BalanceDrift>();

        foreach (var balance in projected)
        {
            var key = (balance.ItemId, balance.LotId, balance.LocationId, balance.LicensePlateId, balance.Status);

            var replayedQuantity = byKey.TryGetValue(key, out var match) ? match.Quantity : 0m;

            if (replayedQuantity != balance.Quantity)
            {
                drift.Add(new BalanceDrift(
                    balance.ItemId,
                    balance.LotId,
                    balance.LocationId,
                    balance.LicensePlateId,
                    balance.Status,
                    balance.Quantity,
                    replayedQuantity,
                    balance.LastMovementSequence));
            }

            byKey.Remove(key);
        }

        // Whatever is left is a holding the ledger knows about and the projection
        // does not — a movement that committed without its balance write, which is
        // the failure the same-transaction rule exists to prevent.
        foreach (var orphan in byKey.Values.Where(r => r.Quantity != 0m))
        {
            drift.Add(new BalanceDrift(
                orphan.ItemId,
                orphan.LotId,
                orphan.LocationId,
                orphan.LicensePlateId,
                orphan.Status,
                Projected: 0m,
                Replayed: orphan.Quantity,
                LastMovementSequence: 0));
        }

        var report = new ReconciliationReport(
            clock.GetUtcNow(),
            movementCount,
            replayed.Count,
            replayed.Sum(r => r.Quantity),
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            drift);

        Report(report);

        return report;
    }

    private void Report(ReconciliationReport report)
    {
        if (report.LedgerSum != 0m)
        {
            // Logged separately and at Critical because it is a different class of
            // problem: drift means the projection is wrong, a non-zero ledger sum
            // means the ledger itself is.
            logger.LogCritical(
                "Stock ledger does not balance: {MovementCount} movements sum to {LedgerSum}, not zero",
                report.MovementsReplayed,
                report.LedgerSum);
        }

        if (report.Drift.Count > 0)
        {
            logger.LogError(
                "Balance projection has drifted on {Count} of {Checked} holdings; worst {Worst}",
                report.Drift.Count,
                report.HoldingsChecked,
                report.Drift.Max(d => Math.Abs(d.Difference)));

            foreach (var drift in report.Drift.Take(20))
            {
                logger.LogError(
                    "Drift: item {ItemId} lot {LotId} at location {LocationId} status {Status} — projected {Projected}, ledger says {Replayed} (last movement #{Sequence})",
                    drift.ItemId,
                    drift.LotId,
                    drift.LocationId,
                    drift.Status,
                    drift.Projected,
                    drift.Replayed,
                    drift.LastMovementSequence);
            }

            return;
        }

        logger.LogInformation(
            "Ledger reconciled clean: {Checked} holdings from {MovementCount} movements",
            report.HoldingsChecked,
            report.MovementsReplayed);
    }
}
