using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// What kind of directed work a task represents.
/// <para>
/// One vocabulary for every phase, even though Phase 3 only raises putaway.
/// A handheld shows an operator their next job; it must not care which module
/// created it, or the offline queue and the task screens get rewritten once per
/// phase.
/// </para>
/// </summary>
public enum WorkTaskKind
{
    Putaway = 1,
    Replenishment = 2,
    Pick = 3,
    CycleCount = 4,

    /// <summary>
    /// Look at what came back and say what it is. Phase 7.
    /// <para>
    /// The only kind that moves no stock and posts nothing. What it produces is
    /// <see cref="InspectionFindings"/> — sealed or opened, repairable or not —
    /// which is the entire input to the disposition decision and the reason a
    /// return is two events rather than one. A disposition decided at the dock door
    /// would be decided by somebody who has not opened the carton.
    /// </para>
    /// </summary>
    Inspection = 5,
    BinCount = 6
}

/// <summary>A job for an operator, raised by whichever module knows it is needed.</summary>
/// <param name="SuggestedLocationId">
/// Where the rules say it should go. Null when nothing was admissible, which is
/// still a task worth raising — the pallet is on the dock either way, and a
/// supervisor needs to see that the system could not place it.
/// </param>
/// <param name="DecisionTraceId">
/// The evaluation behind <paramref name="SuggestedLocationId"/>. Travels onto the
/// ledger entry when the task completes, which is what makes a movement
/// answerable months later.
/// </param>
/// <param name="SourceReference">
/// The document line that caused this, e.g. an ASN line. A free-form string
/// rather than a foreign key: the raising module owns the meaning, and Work must
/// not learn to parse it.
/// </param>
public sealed record WorkTaskRequest
{
    public required WorkTaskKind Kind { get; init; }

    public required Guid WarehouseId { get; init; }

    public required Guid? ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required decimal Quantity { get; init; }

    public required Guid FromLocationId { get; init; }

    public Guid? SuggestedLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public Guid? DecisionTraceId { get; init; }

    /// <summary>
    /// The commitment this job discharges, for a pick. Carried and handed back
    /// untouched — Work never resolves it, exactly as it never resolves
    /// <see cref="DecisionTraceId"/>. It travels here rather than being looked up
    /// at completion because the alternative is a second write stamping the task
    /// onto the reservation, and a crash between the two would leave a
    /// commitment no job can discharge.
    /// </summary>
    public Guid? StockReservationId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    /// <summary>Lower runs first. Defaults to the middle of the band so anything urgent can sort above it.</summary>
    public int Priority { get; init; } = 50;

    public string? SourceReference { get; init; }

    /// <summary>
    /// Makes task creation as repeatable as the posting that accompanies it. A
    /// dock receipt that is retried must not leave two putaway tasks behind, or
    /// the second operator walks to a bin that is already empty.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// Where a job is in its life, for the module that raised it. Mirrors Work's own
/// <c>WorkTaskStatus</c>, published for the same reason <see cref="StockCondition"/>
/// is: the raiser has to be able to tell whether its work happened, and it cannot
/// read another module's tables to find out.
/// </summary>
public enum WorkTaskState
{
    Ready = 1,
    Assigned = 2,
    Completed = 3,
    Cancelled = 4
}

/// <summary>
/// What became of a job, for whoever asked for it.
/// </summary>
/// <param name="CompletedAtLocationId">
/// Where the operator actually confirmed. The raising module needs this rather
/// than the suggestion: an order is despatched from the bench the picker really
/// used, and asking the queue is how outbound learns that without Work having to
/// know outbound exists.
/// </param>
/// <param name="CountedQuantity">
/// What the operator found, on a count. Null on every other kind, and null on a
/// count nobody has confirmed yet — the module that raised the counts learns what
/// they found by asking here, exactly as outbound learns which bench its picks
/// ended up at.
/// </param>
public sealed record WorkTaskSummary(
    Guid Id,
    WorkTaskKind Kind,
    WorkTaskState State,
    decimal Quantity,
    Guid? CompletedAtLocationId,
    DateTimeOffset? CompletedAt,
    string? CompletedByUserName,
    bool WasOverridden,
    decimal? CountedQuantity = null);

/// <summary>
/// The Work module's published surface for directed work raised by another
/// module.
/// <para>
/// Still almost entirely write-only, and the two additions keep the line in the
/// same place. A raiser may add work, ask what became of the work it added, and
/// withdraw work nobody has started — because all three are things it is
/// answerable for. What it may not do is claim or complete a task: those are the
/// operator's, and a contract that exposed them would let a module move stock with
/// nobody standing at the rack.
/// </para>
/// </summary>
public interface IWorkQueue
{
    Task<Result<Guid>> EnqueueAsync(WorkTaskRequest request, CancellationToken cancellationToken);

    /// <summary>Batch lookup by id. Absent ids are simply missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, WorkTaskSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws jobs that have not been started, and returns how many actually
    /// went. If any task is assigned, refuses the whole set under row locks so
    /// callers cannot release source stock while an operator is walking to it.
    /// Completed and already cancelled tasks remain unchanged.
    /// </summary>
    Task<Result<int>> CancelAsync(
        IReadOnlyCollection<Guid> taskIds,
        string reason,
        CancellationToken cancellationToken);
}
