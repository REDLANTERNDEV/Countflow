using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Work;

public static class WorkErrors
{
    public static readonly Error TaskNotFound =
        Error.NotFound("work.task_not_found", "No task with that identifier.");

    public static readonly Error NoWorkAvailable =
        Error.NotFound("work.no_work_available", "There is no work waiting in this warehouse.");

    public static readonly Error TaskAlreadyClaimed =
        Error.Conflict("work.task_already_claimed", "Somebody else is already working on that task.");

    public static readonly Error TaskNotYours =
        Error.Forbidden("work.task_not_yours", "That task is assigned to somebody else.");

    public static readonly Error TaskNotOpen =
        Error.Conflict("work.task_not_open", "That task has already been completed or cancelled.");

    /// <summary>
    /// The task was confirmed once already, under a different key. Not an
    /// idempotent retry — a second confirmation, which against an append-only
    /// ledger would move the stock twice.
    /// </summary>
    public static readonly Error TaskAlreadyCompleted =
        Error.Conflict("work.task_already_completed", "That task has already been confirmed.");

    /// <summary>
    /// A directed movement without a decision trace is a bug (ADR 0004), so
    /// completing rule-driven work that has no trace is refused rather than posted
    /// with a null. Reaching this means the task was raised without asking the
    /// rules, which is a defect in whichever module raised it.
    /// </summary>
    public static readonly Error TaskHasNoDecision =
        Error.Failure(
            "work.task_has_no_decision",
            "That task carries no decision trace, so completing it would post an unexplained movement.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("work.idempotency_key_required", "Every write command must carry an idempotency key.");

    public static readonly Error LocationNotFound =
        Error.NotFound("work.location_not_found", "No location with that identifier or code.");

    public static readonly Error QuantityNotPositive =
        Error.Validation("work.quantity_not_positive", "Quantity must be greater than zero.");

    /// <summary>
    /// Two receipts raced to raise the first task for one pallet and a retry still
    /// could not find the winner. A second failure is not a race, so it surfaces
    /// rather than looping.
    /// </summary>
    public static readonly Error EnqueueRaceLost =
        Error.Conflict("work.enqueue_race_lost", "The task could not be raised. Retry the scan.");

    public static readonly Error WarehouseRequired =
        Error.Validation("work.warehouse_required", "A warehouse is needed to know which queue to read.");
}
