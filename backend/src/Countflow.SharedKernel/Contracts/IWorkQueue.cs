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
    CycleCount = 4
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

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required decimal Quantity { get; init; }

    public required Guid FromLocationId { get; init; }

    public Guid? SuggestedLocationId { get; init; }

    public Guid? LicensePlateId { get; init; }

    public Guid? DecisionTraceId { get; init; }

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

/// <summary>The Work module's published surface for raising directed work.</summary>
public interface IWorkQueue
{
    Task<Result<Guid>> EnqueueAsync(WorkTaskRequest request, CancellationToken cancellationToken);
}
