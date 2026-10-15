using System.ComponentModel.DataAnnotations;

namespace Countflow.Modules.Inventory.Ledger;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Inventory:Reconciliation";

    /// <summary>
    /// On by default. An alarm nobody switched on is not an alarm, and the pass is
    /// a couple of aggregate queries — cheap enough that the reason to disable it
    /// is a test wanting determinism, not cost.
    /// </summary>
    public bool Enabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Deliberately long. Nothing has drifted in the first minute after a deploy,
    /// and a reconciliation pass racing the migrator or a seed would report drift
    /// that is really just a half-written database.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The inventory-owned handler registered with the shared job runner.</summary>
public sealed class PeriodicReconciliation(LedgerReconciler reconciler, ReconciliationJournal journal)
    : Countflow.SharedKernel.Contracts.IBackgroundJob
{
    public async Task ExecuteAsync(Countflow.SharedKernel.Contracts.BackgroundJobRun run, CancellationToken cancellationToken)
    {
        var report = await reconciler.RunAsync(cancellationToken);
        await journal.RecordAsync(report, Domain.ReconciliationTrigger.Periodic, cancellationToken);
    }
}
