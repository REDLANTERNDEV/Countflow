namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// The current quantity of one holding — <c>(item, lot, location, licence plate,
/// status) → quantity</c>.
/// <para>
/// <b>This is a projection, not the truth.</b> It is written in the same
/// transaction as the movement that changes it, and it can be dropped and
/// rebuilt from the ledger at any time (ADR 0001). It exists because "how much
/// of SKU-100 is in A-07-03-02" is asked thousands of times an hour and summing
/// the ledger for every answer would not survive contact with a picking wave.
/// </para>
/// <para>
/// A background reconciler replays the ledger and alarms on any divergence, so
/// the projection being derived is a checkable claim rather than a hope.
/// </para>
/// </summary>
public sealed class StockBalance
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // ---- The key ----
    //
    // Unique as a set, with NULLs compared as equal — without that, Postgres
    // treats every null lot as distinct and the same shelf accumulates a fresh
    // balance row per posting. See InventoryDbContext.

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid LocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public required InventoryStatus Status { get; init; }

    // ---- The value ----

    public decimal Quantity { get; set; }

    /// <summary>
    /// True only for the virtual counterparty locations, which are the contra
    /// accounts of the double-entry scheme and go steadily more negative as goods
    /// arrive from suppliers.
    /// <para>
    /// Copied from the location's type when the row is created rather than
    /// resolved at posting time, because it backs a <c>CHECK</c> constraint: a
    /// check cannot reach into MasterData's tables, and putting the flag here is
    /// what turns "a shelf can never go negative" from a rule the posting code
    /// remembers into one the database enforces on every future code path,
    /// including the ones nobody has written yet.
    /// </para>
    /// </summary>
    public required bool AllowsNegative { get; init; }

    /// <summary>
    /// Sequence of the last movement folded into this row. Lets the reconciler
    /// report *when* a divergence began, and gives an optimistic-concurrency
    /// signal for readers that need one.
    /// </summary>
    public long LastMovementSequence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
