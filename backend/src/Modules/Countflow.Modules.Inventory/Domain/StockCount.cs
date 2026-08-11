namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// What an operator found at one holding, at one moment.
/// <para>
/// <b>Why this is not a ledger entry.</b> The ledger records physical facts: this
/// quantity was in that place, and now it is in this one (ADR 0001). A count
/// asserts that nothing moved — it is an observation <i>about</i> the record
/// rather than a change to it, and the overwhelming majority of counts find
/// exactly what was expected and therefore have nothing to post. Writing those to
/// an append-only record of movements would fill it with rows whose quantity is
/// zero and whose meaning is "no news", and the balance projection, the replay and
/// the reconciler would all have to learn to skip them.
/// </para>
/// <para>
/// And the clean count is the one that matters most. Accuracy is
/// <i>verified</i> over <i>counted</i>; a system that only wrote down the counts
/// that went wrong could report the numerator and would have to guess at the
/// denominator, which is how a warehouse ends up claiming 99% accuracy on the
/// strength of the four bays it looked at.
/// </para>
/// <para>
/// <b>Why it lives here anyway.</b> Beside the ledger, in the same schema and the
/// same context, for the reason <see cref="StockReservation"/> gives: a count that
/// finds a variance has to write this row and post the adjustment together or
/// neither, and two schemas cannot share a transaction (ADR 0002). A count row in
/// a counting module would mean a crash between the two writes leaving either an
/// adjustment nobody can account for or a bay recorded as verified against a
/// correction that never landed.
/// </para>
/// </summary>
public sealed class StockCount
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    // ---- The holding, spelled out rather than named by a balance row id ----
    //
    // The one place in this module that repeats the five parts of a holding
    // instead of pointing at the balance row that already knows them, and it is
    // deliberate. A count must be able to report stock in a place the system
    // believes is empty, and a balance row that has never existed has no id to
    // quote. The row is created by the posting if there is a variance — but the
    // count is recorded either way, including the count that found nothing in a
    // bay the system also thought was empty, which is a real and useful result.

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid LocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public required InventoryStatus Status { get; init; }

    /// <summary>What the book said at the moment the count was confirmed.</summary>
    public required decimal ExpectedQuantity { get; init; }

    /// <summary>What the operator reported. Zero is an answer, not a missing value.</summary>
    public required decimal CountedQuantity { get; init; }

    /// <summary>
    /// Stored rather than computed, and this is the exception that proves the rule
    /// the rest of the system follows.
    /// <para>
    /// Everywhere else a derivable number is derived — a balance is a projection, an
    /// allocated quantity is the sum of its rows. This one is not derivable after
    /// the fact in any useful sense: <see cref="ExpectedQuantity"/> is a reading of
    /// a value that goes on changing, so recomputing the difference tomorrow would
    /// answer a different question. The variance is the finding, and a finding is a
    /// fact about a moment.
    /// </para>
    /// </summary>
    public required decimal Variance { get; init; }

    /// <summary>True when the shelf agreed with the book. The number accuracy is measured from.</summary>
    public bool IsAccurate => Variance == 0m;

    /// <summary>
    /// What the projection's <see cref="StockBalance.LastMovementSequence"/> read
    /// when this count was taken, pinning the observation to a point in the
    /// ledger's history. Zero when the holding had no balance row at all.
    /// <para>
    /// This is what lets the next count ask "has anything happened here since
    /// somebody last checked?" without scanning the ledger, and what lets a
    /// supervisor chasing a variance see exactly which movements the count is
    /// upstream and downstream of.
    /// </para>
    /// </summary>
    public required long AtMovementSequence { get; init; }

    /// <summary>
    /// The adjustment this count posted, or null when there was nothing to post.
    /// Set in the same transaction as that movement.
    /// </summary>
    public Guid? AdjustmentMovementId { get; set; }

    /// <summary>The job that sent somebody to this bin.</summary>
    public Guid? WorkTaskId { get; init; }

    /// <summary>The policy decision that raised the job. See <c>DecisionKind.CycleCount</c>.</summary>
    public Guid? DecisionTraceId { get; init; }

    /// <summary>
    /// Unique. A handheld that retries a confirmation must not record the bay as
    /// counted twice — which would quietly double the denominator of every accuracy
    /// figure the product's central claim is made on.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required Guid CountedByUserId { get; init; }

    public required string CountedByUserName { get; init; }

    public required DateTimeOffset CountedAt { get; init; }

    public string? Notes { get; init; }
}
