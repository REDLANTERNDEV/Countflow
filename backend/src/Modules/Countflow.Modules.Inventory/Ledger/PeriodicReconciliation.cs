using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

/// <summary>
/// Runs <see cref="LedgerReconciler"/> on a timer and alarms through the log.
/// <para>
/// Read-only, which is what makes it safe to run on every API replica: two
/// replicas both replaying the ledger duplicate work but cannot corrupt anything,
/// so this needs no leader election. If the pass ever grows expensive enough for
/// the duplication to matter, that is the point to reach for an advisory lock —
/// not before.
/// </para>
/// </summary>
public sealed class PeriodicReconciliation(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconciliationOptions> options,
    TimeProvider clock,
    ILogger<PeriodicReconciliation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Ledger reconciliation is disabled");
            return;
        }

        logger.LogInformation(
            "Ledger reconciliation starts in {InitialDelay} then every {Interval}",
            settings.InitialDelay,
            settings.Interval);

        try
        {
            await Task.Delay(settings.InitialDelay, clock, stoppingToken);

            using var timer = new PeriodicTimer(settings.Interval, clock);

            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Its own scope: a background loop must not hold a request-scoped
            // DbContext open for the lifetime of the process.
            await using var scope = scopeFactory.CreateAsyncScope();

            var reconciler = scope.ServiceProvider.GetRequiredService<LedgerReconciler>();
            await reconciler.RunAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed pass must not take the host down: the reconciler is a
            // watchdog, and a watchdog that kills the process it watches is worse
            // than no watchdog.
            logger.LogError(exception, "Ledger reconciliation pass failed");
        }
    }
}
