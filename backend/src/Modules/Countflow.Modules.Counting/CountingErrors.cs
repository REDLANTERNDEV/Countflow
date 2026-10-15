using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Counting;

public static class CountingErrors
{
    public static readonly Error ScheduleBudgetInvalid =
        Error.Validation("counting.schedule_budget_invalid", "A schedule requires 1–200 tasks.");
    public static readonly Error ScheduleIntervalInvalid =
        Error.Validation("counting.schedule_interval_invalid", "A schedule interval requires 1–10080 minutes.");
    public static readonly Error PlanIncomplete =
        Error.Conflict("counting.plan_incomplete", "Some count tasks could not be resolved; retry cancellation.");

    public static readonly Error PlanNotFound =
        Error.NotFound("counting.plan_not_found", "No count plan with that identifier.");

    public static readonly Error WarehouseNotFound =
        Error.NotFound("counting.warehouse_not_found", "No warehouse with that code.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("counting.idempotency_key_required", "Every write command must carry an idempotency key.");

    public static readonly Error TaskCountNotPositive =
        Error.Validation("counting.task_count_not_positive", "A count plan has to ask for at least one job.");

    /// <summary>
    /// A shift's counting budget is measured in jobs somebody will actually walk to.
    /// Refused rather than silently trimmed: a supervisor who asked for a thousand
    /// counts has misunderstood something, and quietly giving them fifty would let
    /// them go on misunderstanding it.
    /// </summary>
    public static Error TooManyTasks(int limit) =>
        Error.Validation(
            "counting.too_many_tasks",
            $"A single plan may raise at most {limit} counts. Raise another plan when these are done.");

    public static readonly Error PlanNotOpen =
        Error.Conflict("counting.plan_not_open", "That plan has already been completed or cancelled.");

    /// <summary>
    /// Some of the plan's counts are on somebody's handheld. Mirrors the outbound
    /// cancellation rule and for the same reason: an operator halfway to the rack is
    /// not something a supervisor gets to withdraw out from under them.
    /// </summary>
    public static Error CountsInProgress(int count) =>
        Error.Conflict(
            "counting.counts_in_progress",
            $"{count} of this plan's counts are claimed by an operator and cannot be withdrawn.");
}
