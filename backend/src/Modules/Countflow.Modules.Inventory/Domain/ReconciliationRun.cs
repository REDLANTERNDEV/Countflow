namespace Countflow.Modules.Inventory.Domain;

/// <summary>What asked for a reconciliation pass.</summary>
public enum ReconciliationTrigger
{
    /// <summary>The background timer. The overwhelming majority.</summary>
    Periodic = 1,

    /// <summary>Somebody pressed the button on the ledger screen.</summary>
    Manual = 2
}

/// <summary>
/// The outcome of one reconciliation pass, written down.
/// <para>
/// The reconciler itself stays read-only: it replays the ledger, compares it with
/// the projection and reports (see <c>LedgerReconciler</c>). What Phase 5 adds is
/// that somebody keeps the reports, because a dashboard that claimed "the stock is
/// right" would otherwise have to work that out a second way — and two independent
/// answers to "is the stock right" is precisely the problem the reconciler exists
/// to prevent. The screen reads what the reconciler produced or it says nothing.
/// </para>
/// <para>
/// One row per pass rather than one row per drifting holding. Drift is expected to
/// be zero forever, so the interesting series is the run history — when it last
/// ran, how much it checked, and whether it has ever found anything. The detail of
/// a drifting holding is in the log, at Error, where somebody is paged for it.
/// </para>
/// </summary>
public sealed class ReconciliationRun
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DateTimeOffset RunAt { get; init; }

    public required ReconciliationTrigger Trigger { get; init; }

    public required long MovementsReplayed { get; init; }

    public required int HoldingsChecked { get; init; }

    /// <summary>
    /// The sum of every signed posting. Must be exactly zero; anything else means
    /// the ledger itself is wrong rather than the projection, which is a deeper
    /// failure and is why it is stored beside the drift count rather than folded
    /// into it.
    /// </summary>
    public required decimal LedgerSum { get; init; }

    public required int DriftCount { get; init; }

    /// <summary>Largest absolute difference found, in base units. Zero on a clean run.</summary>
    public required decimal WorstDrift { get; init; }

    /// <summary>How long the pass took. The number that decides when replay stops scaling.</summary>
    public required long ElapsedMilliseconds { get; init; }

    public bool IsClean => DriftCount == 0 && LedgerSum == 0m;
}
