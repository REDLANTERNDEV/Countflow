using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Work;

public static class WorkErrors
{
    public static readonly Error CompletionPending = Error.Conflict("work.completion_pending", "Retry the original pending confirmation before changing this pallet task.");
    public static readonly Error CompletionKeyConflict = Error.Conflict("work.completion_key_conflict", "That confirmation key belongs to a different request.");
    public static readonly Error CompletionRequestInvalid = Error.Validation("work.completion_request_invalid", "A pallet confirmation needs a key of at most 124 characters, a destination, and an override reason of at most 500 characters.");
    public static readonly Error ReslotRequestInvalid = Error.Validation("work.reslot_request_invalid", "A re-slot needs a retry key and a reason of 1–500 characters.");
    public static readonly Error ReslotKeyConflict = Error.Conflict("work.reslot_key_conflict", "That re-slot key belongs to a different reason.");
    public static readonly Error NotPalletPutaway = Error.Conflict("work.not_pallet_putaway", "Only a whole-pallet putaway can be re-slotted.");
    public static readonly Error PalletEmpty = Error.Conflict("work.pallet_empty", "This pallet has no stock at the task's origin.");
    public static readonly Error PutawayDecisionChanged = Error.Conflict("work.putaway_decision_changed", "The pallet decision changed. Refresh the task before confirming.");
    public static readonly Error PutawayContentsChanged = Error.Conflict("work.putaway_contents_changed", "The pallet contents changed. A supervisor must re-slot it before it can be taken.");
    public static readonly Error ReceiptPending = Error.Conflict("work.receipt_pending", "Finish or retry the pending pallet receipt before taking this task.");
    public static readonly Error PalletInUse = Error.Conflict("work.pallet_in_use", "Hand this pallet's task back before receiving more onto it.");
    public static readonly Error ReceiptKeyConflict = Error.Conflict("work.receipt_key_conflict", "That receipt key belongs to a different request.");
    public static readonly Error ReceiptInvalid = Error.Validation("work.receipt_invalid", "The receipt handoff information is invalid.");
    public static readonly Error ReplenishmentDestinationMismatch =
        Error.Validation("work.replenishment_destination_mismatch", "Scan the replenishment task's pick face.");

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

    /// <summary>
    /// A pick with no commitment behind it. The outbound twin of
    /// <see cref="TaskHasNoDecision"/>: reaching it means whichever module raised
    /// the job never reserved the stock, and confirming it would take goods out of
    /// a bin against nobody's order.
    /// </summary>
    public static readonly Error TaskHasNoReservation =
        Error.Failure(
            "work.task_has_no_reservation",
            "That pick carries no reservation, so confirming it would take stock nobody had claimed.");

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

    /// <summary>
    /// A count confirmed with no number. The counting twin of
    /// <see cref="TaskHasNoDecision"/> and <see cref="TaskHasNoReservation"/>: the
    /// one thing the job exists to collect is missing, and closing it without one
    /// would mark a bay verified on the strength of nothing.
    /// </summary>
    public static readonly Error CountQuantityRequired =
        Error.Validation(
            "work.count_quantity_required",
            "A cycle count is confirmed with the quantity found, and zero is a valid answer.");

    /// <summary>
    /// The operator scanned a different bay from the one the count names.
    /// <para>
    /// Refused rather than recorded as an override, which is the difference between
    /// a count and every other kind of job. Confirming a putaway somewhere else is a
    /// judgement about where stock should live and is worth capturing; confirming a
    /// count somewhere else is an answer to a question nobody asked, and posting it
    /// would adjust one holding on the strength of a look at another.
    /// </para>
    /// </summary>
    public static readonly Error CountLocationMismatch =
        Error.Validation(
            "work.count_location_mismatch",
            "A cycle count is confirmed at the location it names. Scan that bay, or reject the task.");

    public static readonly Error TourNotFound =
        Error.NotFound("work.tour_not_found", "No pick tour with that identifier.");

    public static readonly Error TourNotYours =
        Error.Forbidden("work.tour_not_yours", "That tour belongs to somebody else.");

    /// <summary>
    /// Every stop on the tour has been walked or has already gone back. Reported
    /// rather than shrugged off, because a scanner that shows "released" over a tour
    /// somebody else is standing in the middle of is worse than a refusal.
    /// </summary>
    public static readonly Error TourNothingToRelease =
        Error.Conflict("work.tour_nothing_to_release", "There is nothing left on that tour to hand back.");

    public static readonly Error WarehouseRequired =
        Error.Validation("work.warehouse_required", "A warehouse is needed to know which queue to read.");
}
