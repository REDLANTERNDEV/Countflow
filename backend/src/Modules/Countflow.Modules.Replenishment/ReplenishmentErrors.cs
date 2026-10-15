using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Replenishment;

public static class ReplenishmentErrors
{
    public static readonly Error LevelsInvalid = Error.Validation("replenishment.levels_invalid", "Use positive levels with maximum greater than minimum, at most four decimal places.");
    public static readonly Error CapacityExceeded = Error.Validation("replenishment.capacity_exceeded", "Maximum exceeds the pick face capacity.");
    public static readonly Error FaceInvalid = Error.Validation("replenishment.face_invalid", "Choose an active replenishable pick face.");
    public static readonly Error ItemNotFound = Error.NotFound("replenishment.item_not_found", "Choose an active item.");
    public static readonly Error StockCommitted = Error.Conflict("replenishment.stock_committed", "Maximum cannot be reduced below committed stock. Finish or withdraw incoming work first.");
    public static readonly Error InProgress = Error.Conflict("replenishment.in_progress", "Finish or cancel outstanding replenishments before changing the item.");
    public static readonly Error PlanNotFound = Error.NotFound("replenishment.plan_not_found", "No replenishment plan with that identifier.");
    public static readonly Error RetryCancellation = Error.Conflict("replenishment.retry_cancellation", "Some tasks could not be resolved. Retry cancellation.");
}
