using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Work.Domain;

/// <summary>
/// Where a task is in its life.
/// <para>
/// Four states, and no "InProgress". A putaway is atomic from the system's point
/// of view — the operator either confirms the drop or does not — and a state that
/// exists only between two scans on the same screen buys nothing but a way for
/// tasks to get stuck in it.
/// </para>
/// </summary>
public enum WorkTaskStatus
{
    /// <summary>Waiting for somebody to take it.</summary>
    Ready = 1,

    /// <summary>Claimed. Nobody else will be offered it.</summary>
    Assigned = 2,

    Completed = 3,

    Cancelled = 4
}

/// <summary>
/// One job for one operator.
/// <para>
/// Deliberately generic across kinds. Phase 3 only raises putaway, but picks,
/// replenishments and counts are the same shape — take stock from here, confirm
/// it at there — and a handheld that learned a new screen per phase would be
/// three unrelated apps by Phase 5.
/// </para>
/// <para>
/// The task is not the movement. It records what was <i>intended</i> and what the
/// operator actually did; the ledger records what happened. Keeping them separate
/// is what makes an override visible: <see cref="SuggestedLocationId"/> and
/// <see cref="CompletedAtLocationId"/> differing is the measurable signal that the
/// rules and the floor disagree, and it is the number that tells a supervisor
/// their rule set needs work.
/// </para>
/// </summary>
public sealed class WorkTask
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required WorkTaskKind Kind { get; init; }

    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Ready;

    public required Guid WarehouseId { get; init; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; set; } = 50;

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    /// <summary>
    /// In base units, and a projection of <see cref="Sources"/> rather than a
    /// figure anybody sets.
    /// <para>
    /// Informational for a whole-plate move, where the pallet is the unit of work
    /// and its contents are read from the ledger at completion. It is still worth
    /// being right: it is what the operator sees before they walk over.
    /// </para>
    /// </summary>
    public decimal Quantity { get; set; }

    public required Guid FromLocationId { get; init; }

    /// <summary>Where the rules said it should go. Null when nothing was admissible.</summary>
    public Guid? SuggestedLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    /// <summary>
    /// The evaluation behind <see cref="SuggestedLocationId"/>. Present even when
    /// no location was suggested — "nothing fitted, and here is why each candidate
    /// failed" is the most useful trace of all.
    /// </summary>
    public Guid? DecisionTraceId { get; init; }

    /// <summary>
    /// The commitment a pick discharges. Carried, never interpreted — this module
    /// has no idea what a reservation is, in exactly the way it has no idea what a
    /// decision trace contains, and both are handed back untouched to the module
    /// that understands them.
    /// <para>
    /// Null on every kind but <see cref="WorkTaskKind.Pick"/>, and completing a
    /// pick without one is refused rather than posted: stock leaving a bin against
    /// no claim is the outbound equivalent of a directed movement with no trace.
    /// </para>
    /// </summary>
    public Guid? StockReservationId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    /// <summary>The document line that caused this — an ASN line reference. Opaque to this module.</summary>
    public string? SourceReference { get; init; }

    /// <summary>
    /// What caused this task, one row per contribution. See
    /// <see cref="WorkTaskSource"/>.
    /// </summary>
    public ICollection<WorkTaskSource> Sources { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // ---- Assignment ----

    public Guid? AssignedToUserId { get; set; }

    public string? AssignedToUserName { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    // ---- Completion ----

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedAtLocationId { get; set; }

    public Guid? CompletedByUserId { get; set; }

    public string? CompletedByUserName { get; set; }

    /// <summary>True when the operator confirmed somewhere other than the suggestion.</summary>
    public bool WasOverridden { get; set; }

    /// <summary>Why, if they said. Optional on purpose — a mandatory free-text box on a scanner collects the letter x.</summary>
    public string? OverrideReason { get; set; }

    /// <summary>
    /// The key the completion posted under. Stored so a retry with the same key
    /// replays, and a <i>different</i> key against an already-completed task is
    /// recognised as the second confirmation it is rather than silently accepted.
    /// </summary>
    public string? CompletionIdempotencyKey { get; set; }

    public string? CancelledReason { get; set; }
}

/// <summary>
/// One thing that asked for this task to exist.
/// <para>
/// Usually exactly one — a dock receipt raises a putaway. A mixed pallet is the
/// case that makes this a table: the trailer is unloaded SKU by SKU onto one
/// pallet, so several receipts contribute to one job, and the operator still
/// picks the pallet up once.
/// </para>
/// <para>
/// Same argument as <c>AsnReceipt</c> and the ledger above it (ADR 0001):
/// <see cref="WorkTask.Quantity"/> is a projection and these rows are what it
/// projects. An accumulating total cannot be made idempotent — a retried receipt
/// that adds its quantity a second time sends the operator looking for stock that
/// is not there — but an append with a unique key can only land once.
/// </para>
/// </summary>
public sealed class WorkTaskSource
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WorkTaskId { get; init; }

    /// <summary>The raiser's key. Unique across every task, which is what makes enqueuing exactly-once.</summary>
    public required string IdempotencyKey { get; init; }

    public required decimal Quantity { get; init; }

    public required DateTimeOffset AddedAt { get; init; }

    public WorkTask? Task { get; init; }
}
