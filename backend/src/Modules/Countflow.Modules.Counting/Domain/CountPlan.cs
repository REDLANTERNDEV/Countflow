using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Counting.Domain;

/// <summary>
/// Where a plan is in its life.
/// <para>
/// Three states and no "in progress". A plan is open until every job it raised has
/// been confirmed or withdrawn, and a state that existed only while somebody was
/// walking would be a state nothing sets and nothing reads.
/// </para>
/// </summary>
public enum CountPlanStatus
{
    Open = 1,

    /// <summary>Every job it raised has been confirmed.</summary>
    Completed = 2,

    Cancelled = 3
}

/// <summary>
/// One run of the counting policy: what the rules said was due, and the jobs that
/// went out because of it.
/// <para>
/// The counting equivalent of an advice note or a sales order — the document the
/// work hangs off. It exists for two reasons that are worth separating. The
/// obvious one is that somebody has to be able to look at last Tuesday's counts as
/// a set. The load-bearing one is that <b>this table is what the policy reads to
/// know what is already scheduled</b>: without it, every planning run would
/// re-propose the same overdue bays that are already on somebody's handheld, and
/// the queue would fill with duplicates of the worst holdings in the building.
/// </para>
/// <para>
/// It records what was <i>asked for</i>. What was <i>found</i> lives in the
/// <c>inventory</c> schema, where it belongs — a count is a statement about stock,
/// and this module is an orchestrator that writes no stock figures of its own.
/// </para>
/// </summary>
public sealed class CountPlan
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>Human reference, e.g. <c>CNT-2026-0007</c>. Unique within a warehouse.</summary>
    public required string Reference { get; set; }

    public CountPlanStatus Status { get; set; } = CountPlanStatus.Open;

    /// <summary>How many jobs were asked for. What was raised is <see cref="Lines"/>, and may be fewer.</summary>
    public required int RequestedTasks { get; init; }

    /// <summary>
    /// The policy evaluation behind the whole plan. One trace for the run rather
    /// than one per line, because one evaluation produced the whole ranking — the
    /// same shape as an allocation, where one decision yields several lines.
    /// </summary>
    public required Guid DecisionTraceId { get; init; }

    /// <summary>
    /// Makes planning as repeatable as the postings downstream of it. A retried
    /// request must converge on the plan it already created rather than running the
    /// policy again and raising a second set of jobs against the same bays.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public required string CreatedByUserName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Notes { get; set; }

    public string? CancelledReason { get; set; }

    public ICollection<CountPlanLine> Lines { get; init; } = [];
}

/// <summary>
/// One holding the plan asked somebody to go and look at.
/// <para>
/// Written before the job is raised, and that ordering is the whole of the
/// convergence argument (see <c>CountPlanning</c>). A line with no
/// <see cref="WorkTaskId"/> is a count this module intended and has not yet
/// managed to raise, which the next run picks up; a job raised before its line was
/// recorded would be work nothing knows it asked for.
/// </para>
/// </summary>
public sealed class CountPlanLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CountPlanId { get; init; }

    /// <summary>
    /// The balance row the policy chose. Opaque here — this module never resolves
    /// it — and the identifier the policy's already-scheduled exclusion is keyed on.
    /// </summary>
    public required Guid BalanceId { get; init; }

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required Guid LocationId { get; init; }

    /// <summary>Denormalised for the plan screen, which is read far more often than it is written.</summary>
    public required string LocationCode { get; init; }

    public Guid? LicensePlateId { get; init; }

    public StockCondition Condition { get; init; } = StockCondition.Available;

    /// <summary>
    /// What the book said when the plan was made. Shown so a supervisor can see
    /// what the counter was walking towards, and deliberately not what the variance
    /// is computed against — Inventory reads the book again at the moment of
    /// confirmation, because a pick in between is a real movement rather than an
    /// error (see <c>StockCounting</c>).
    /// </summary>
    public required decimal ExpectedQuantity { get; init; }

    /// <summary>Position in the policy's ranking, and the score behind it. Both read straight off the trace.</summary>
    public required int Rank { get; init; }

    public required decimal Score { get; init; }

    /// <summary>The job raised for this line. Null until it has been.</summary>
    public Guid? WorkTaskId { get; set; }

    /// <summary>Derived from the plan's, one per line. What makes a retried planning run raise each job once.</summary>
    public required string IdempotencyKey { get; init; }

    public CountPlan? Plan { get; init; }
}
