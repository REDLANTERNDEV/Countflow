using Countflow.Modules.Work.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Work.Features;

/// <summary>
/// A task as an operator or a supervisor sees it: identifiers resolved to the
/// codes printed on labels, because nobody scans a GUID.
/// </summary>
/// <param name="SuggestedLocationCode">
/// Where the rules said to go. Null when nothing was admissible — the task is
/// still real, and <paramref name="DecisionTraceId"/> still explains it.
/// </param>
public sealed record WorkTaskResponse(
    Guid Id,
    WorkTaskKind Kind,
    WorkTaskStatus Status,
    int Priority,
    string Sku,
    string Description,
    string BaseUom,
    decimal Quantity,
    string? LotNumber,
    DateOnly? ExpiryDate,
    string? Sscc,
    string FromLocationCode,
    string? SuggestedLocationCode,
    Guid? DecisionTraceId,
    StockCondition Condition,
    string? SourceReference,
    DateTimeOffset CreatedAt,
    string? AssignedToUserName,
    DateTimeOffset? CompletedAt,
    string? CompletedAtLocationCode,
    bool WasOverridden,
    string? OverrideReason,
    decimal? CountedQuantity);

/// <summary>
/// A task together with the reasoning behind its suggestion.
/// <para>
/// Returned as one payload rather than leaving the client to fetch the trace
/// separately. The handheld's "why here?" has to work on a wireless link that
/// drops in the racking, and a second round trip is the one most likely to fail.
/// </para>
/// </summary>
public sealed record WorkTaskDetailResponse(WorkTaskResponse Task, DecisionTraceView? Decision);

public sealed record WorkTaskPage(IReadOnlyList<WorkTaskResponse> Items, int Total, int Page, int PageSize);

/// <summary>What the operator confirmed, and where.</summary>
/// <param name="ToLocationCode">
/// The location actually scanned. Differing from the suggestion is not an error —
/// it is the signal the rule set needs tuning, and it is recorded as such. On a
/// count it is the bay being counted, and it has to match: the job asks about one
/// holding, so a different code is a mis-scan rather than a judgement.
/// </param>
/// <param name="CountedQuantity">
/// What was found, on a count and on nothing else. The whole of Phase 5's addition
/// to this contract — every other kind of job is fully described by where it was
/// confirmed, and a count is described by a number instead.
/// </param>
public sealed record CompleteWorkTaskRequest(
    string ToLocationCode,
    string? OverrideReason = null,
    decimal? CountedQuantity = null);

/// <param name="Movements">
/// One entry per ledger line posted. A whole-pallet putaway carrying three SKUs
/// posts three, under one idempotency key family and in one transaction. <b>A count
/// that agreed with the book posts none</b>, and that empty list is a success.
/// </param>
/// <param name="Count">
/// What the count found, when the job was one: the book figure, the reported one
/// and the difference. Present even when nothing was posted — especially then,
/// because "you and the system agree" is the whole answer the operator is owed and
/// there is no movement to carry it.
/// </param>
public sealed record CompleteWorkTaskResponse(
    WorkTaskResponse Task,
    IReadOnlyList<PostedMovement> Movements,
    CountOutcome? Count = null);
