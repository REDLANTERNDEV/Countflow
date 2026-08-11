using Countflow.Modules.Counting.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Counting.Features.Plans;

/// <param name="MaxTasks">
/// How many counts to raise. A shift's counting budget, in jobs — see
/// <c>CountPlanning.MaxTasksPerPlan</c> for why this is capped rather than
/// unbounded.
/// </param>
public sealed record CreateCountPlanRequest(
    string WarehouseCode,
    int MaxTasks = 20,
    string? Notes = null);

/// <param name="State">
/// What became of the job, asked of the work queue rather than mirrored here. This
/// module raises counts and never learns their fate any other way — the same
/// division outbound keeps with its picks.
/// </param>
/// <param name="CountedQuantity">What the operator reported, once they have. Null until then.</param>
/// <param name="Variance">
/// Reported figure minus the figure the plan was made against. Indicative only, and
/// deliberately labelled as such: the authoritative variance is the one Inventory
/// computed at the moment of confirmation, against a book that may have moved
/// legitimately since the plan was drawn.
/// </param>
public sealed record CountPlanLineResponse(
    Guid Id,
    string Sku,
    string? LotNumber,
    string LocationCode,
    StockCondition Condition,
    decimal ExpectedQuantity,
    int Rank,
    decimal Score,
    Guid? WorkTaskId,
    WorkTaskState? State,
    decimal? CountedQuantity,
    decimal? Variance,
    DateTimeOffset? CountedAt,
    string? CountedByUserName);

public sealed record CountPlanResponse(
    Guid Id,
    string Reference,
    CountPlanStatus Status,
    string WarehouseCode,
    int RequestedTasks,
    int RaisedCount,
    int CompletedCount,
    int VarianceCount,
    Guid DecisionTraceId,
    DateTimeOffset CreatedAt,
    string CreatedByUserName,
    DateTimeOffset? CompletedAt,
    string? Notes,
    IReadOnlyList<CountPlanLineResponse> Lines);

public sealed record CountPlanSummaryResponse(
    Guid Id,
    string Reference,
    CountPlanStatus Status,
    int RequestedTasks,
    int RaisedCount,
    int CompletedCount,
    int VarianceCount,
    Guid DecisionTraceId,
    DateTimeOffset CreatedAt,
    string CreatedByUserName);

public sealed record CountPlanPage(
    IReadOnlyList<CountPlanSummaryResponse> Items,
    int Total,
    int Page,
    int PageSize);
