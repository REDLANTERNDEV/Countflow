using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Keeps what the reconciler found.
/// <para>
/// Separate from <see cref="LedgerReconciler"/> on purpose, and the separation is
/// the same one <c>DecisionJournal</c> makes: the thing that works out the answer
/// and the thing that writes it down are different jobs, and keeping the reconciler
/// a pure read is worth protecting. Its whole standing rests on being unable to
/// touch the numbers it is auditing, and a class that both replayed the ledger and
/// wrote to the schema would have given that up for a convenience.
/// </para>
/// <para>
/// <b>Why keep them at all.</b> The accuracy dashboard has to answer "is the stock
/// right?", and there is exactly one thing in this system entitled to answer that.
/// A screen that recomputed drift its own way would be a second opinion, and two
/// answers to that question is the precise failure the reconciler exists to
/// prevent — so the screen reads what the reconciler produced, or it says nothing.
/// </para>
/// </summary>
public sealed class ReconciliationJournal(InventoryDbContext dbContext)
{
    public async Task<ReconciliationRun> RecordAsync(
        ReconciliationReport report,
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken)
    {
        var run = new ReconciliationRun
        {
            RunAt = report.RunAt,
            Trigger = trigger,
            MovementsReplayed = report.MovementsReplayed,
            HoldingsChecked = report.HoldingsChecked,
            LedgerSum = report.LedgerSum,
            DriftCount = report.Drift.Count,
            WorstDrift = report.WorstDrift,
            ElapsedMilliseconds = (long)report.Elapsed.TotalMilliseconds
        };

        dbContext.ReconciliationRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);

        return run;
    }

    /// <summary>
    /// The recent history, newest first.
    /// <para>
    /// A window rather than everything. Hourly passes make a year of history nine
    /// thousand rows, and what a supervisor wants from the screen is "has it been
    /// running, and has it ever found anything" — which the last few days answer
    /// as well as the last few years.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationRun>> RecentAsync(
        int limit,
        CancellationToken cancellationToken) =>
        await dbContext.ReconciliationRuns
            .AsNoTracking()
            .OrderByDescending(r => r.RunAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
}
