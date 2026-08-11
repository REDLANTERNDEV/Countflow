using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>What the rules are being asked to find work for.</summary>
/// <param name="MaxTasks">
/// How many counts to raise. A shift has a counting budget measured in jobs, not
/// in bins that happen to be due — asking for "everything overdue" on the first
/// run of a warehouse that has never counted would raise two thousand tasks and
/// teach everybody to ignore the queue.
/// </param>
/// <param name="AlreadyScheduledBalanceIds">
/// Holdings that already have a count outstanding. Passed in by the caller that
/// owns that fact rather than looked up here, and offered to the rule set as a
/// candidate to exclude rather than filtered out of the query, so the trace can
/// say the bay was considered and is already on somebody's list.
/// </param>
public sealed record CountPolicyQuery
{
    public required Guid WarehouseId { get; init; }

    public required int MaxTasks { get; init; }

    public IReadOnlyCollection<Guid> AlreadyScheduledBalanceIds { get; init; } = [];

    public required PostingActor Actor { get; init; }
}

/// <summary>
/// One holding the policy says is due, and what the system currently believes is
/// in it.
/// </summary>
/// <param name="ExpectedQuantity">
/// The book quantity when the count was raised. Recorded so a supervisor can see
/// what the operator was walking towards; deliberately <b>not</b> what the
/// variance is computed against — that is read again at the moment the count is
/// confirmed, because a pick between the two is a real movement and not an error.
/// </param>
public sealed record CountDueLine(
    Guid BalanceId,
    Guid ItemId,
    Guid? LotId,
    string? LotNumber,
    Guid LocationId,
    string LocationCode,
    Guid? LicensePlateId,
    StockCondition Condition,
    decimal ExpectedQuantity,
    int Rank,
    decimal Score);

/// <summary>
/// The rules' answer to "what should we count next", and the trace that justifies
/// it.
/// <para>
/// Empty <see cref="Lines"/> with a live <see cref="DecisionTraceId"/> is an
/// ordinary and rather satisfying answer: nothing is due, and the trace says why
/// each holding was passed over. It is the same asymmetry as
/// <see cref="AllocationPlan"/> and <see cref="PutawayAdvice"/>, and the reason
/// all three keep the trace outside the nullable part.
/// </para>
/// </summary>
public sealed record CountPlanAdvice(
    Guid DecisionTraceId,
    IReadOnlyList<CountDueLine> Lines,
    int ConsideredCount,
    int AdmissibleCount);

/// <summary>
/// The Rules module's published advisory surface for choosing what to count.
/// <para>
/// The third vocabulary, and the one that finally makes the engine's generality a
/// claim rather than a coincidence. Putaway ranks two thousand empty bins;
/// allocation ranks a handful of holdings against a demand; this ranks the whole
/// warehouse against nothing but its own history, and takes the top of the list.
/// Three decisions with nothing in common but their shape, and one evaluator.
/// </para>
/// <para>
/// Unlike the other two it produces work rather than a movement, which changes
/// nothing about the contract: the trace is committed before this returns, so the
/// caller holds an identifier to stamp onto every task it is about to raise.
/// </para>
/// </summary>
public interface ICountPolicy
{
    Task<Result<CountPlanAdvice>> AdviseAsync(
        CountPolicyQuery query,
        CancellationToken cancellationToken);
}
