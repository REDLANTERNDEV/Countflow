using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Outbound;

public static class OutboundErrors
{
    public static readonly Error OrderNotFound =
        Error.NotFound("outbound.order_not_found", "No order with that identifier or reference.");

    public static readonly Error ItemNotFound =
        Error.NotFound("outbound.item_not_found", "No item with that SKU.");

    public static readonly Error LocationNotFound =
        Error.NotFound("outbound.location_not_found", "No location with that code.");

    public static readonly Error WarehouseNotFound =
        Error.NotFound("outbound.warehouse_not_found", "No warehouse with that code.");

    public static readonly Error WaveNotFound =
        Error.NotFound("outbound.wave_not_found", "No wave with that identifier.");

    public static readonly Error OrderReferenceTaken =
        Error.Conflict("outbound.order_reference_taken", "That order reference is already in use.");

    public static readonly Error WaveReferenceTaken =
        Error.Conflict("outbound.wave_reference_taken", "That wave reference is already in use.");

    public static readonly Error NoLines =
        Error.Validation("outbound.no_lines", "An order needs at least one line.");

    public static readonly Error NoOrders =
        Error.Validation("outbound.no_orders", "A wave needs at least one order.");

    public static readonly Error QuantityNotPositive =
        Error.Validation("outbound.quantity_not_positive", "Quantity must be greater than zero.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("outbound.idempotency_key_required", "Every write command must carry an idempotency key.");

    /// <summary>
    /// The picks have to be dropped somewhere, and a warehouse cannot guess where.
    /// Choosing a bench is a real decision about congestion and travel; the system
    /// asks rather than picking one it could not explain.
    /// </summary>
    public static readonly Error StagingLocationRequired =
        Error.Validation(
            "outbound.staging_required",
            "An order needs a pack bench or staging lane before it can be released.");

    public static Error NotAStagingLocation(string code) =>
        Error.Validation(
            "outbound.not_a_staging_location",
            $"Location {code} is not a pack bench, staging lane or shipping door.");

    public static Error OrderNotReleasable(string status) =>
        Error.Conflict("outbound.order_not_releasable", $"An order in state {status} cannot be released.");

    /// <summary>
    /// Packing is a claim that the goods are made up and standing at the bench, so
    /// it cannot be made while somebody is still walking towards a rack for them.
    /// </summary>
    public static Error PicksOutstanding(int outstanding) =>
        Error.Conflict(
            "outbound.picks_outstanding",
            $"{outstanding} picks are still open. The order cannot be packed until they are confirmed.");

    public static Error OrderNotPackable(string status) =>
        Error.Conflict("outbound.order_not_packable", $"An order in state {status} cannot be packed.");

    public static Error OrderNotDespatchable(string status) =>
        Error.Conflict("outbound.order_not_despatchable", $"An order in state {status} cannot be despatched.");

    /// <summary>
    /// A second despatch under a new key. Not an idempotent retry — a second
    /// trailer's worth of stock leaving against one order.
    /// </summary>
    public static readonly Error AlreadyDespatched =
        Error.Conflict("outbound.already_despatched", "That order has already been despatched.");

    public static Error OrderNotCancellable(string status) =>
        Error.Conflict("outbound.order_not_cancellable", $"An order in state {status} cannot be cancelled.");

    /// <summary>
    /// Somebody is holding a pick for this order. Releasing the stock underneath
    /// them would send them to fetch goods against a claim that no longer exists,
    /// so the cancellation waits for them to finish or hand the job back.
    /// </summary>
    public static Error PicksInProgress(int claimed) =>
        Error.Conflict(
            "outbound.picks_in_progress",
            $"{claimed} picks are claimed by operators. Ask them to release the jobs, then cancel.");

    public static readonly Error NothingPicked =
        Error.Conflict(
            "outbound.nothing_picked",
            "No stock was picked for this order, so there is nothing to despatch.");

    public static readonly Error WaveNotReleasable =
        Error.Conflict("outbound.wave_not_releasable", "That wave has already been released or cancelled.");

    public static Error OrderNotWaveable(string reference) =>
        Error.Conflict(
            "outbound.order_not_waveable",
            $"Order {reference} is already released, waved or closed, so it cannot join a wave.");
}
