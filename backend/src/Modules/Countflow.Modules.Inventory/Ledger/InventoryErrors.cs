using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Every way a ledger command can legitimately fail, as a stable code.
/// <para>
/// The description is for the developer reading a log; the <c>code</c> is the
/// contract. Clients render wording from <c>packages/i18n</c> keyed on the code,
/// so an operator on a Turkish handheld sees Turkish without the backend
/// shipping translations.
/// </para>
/// </summary>
public static class InventoryErrors
{
    public static readonly Error ItemNotFound =
        Error.NotFound("inventory.item_not_found", "No item with that identifier.");

    public static readonly Error ItemInactive =
        Error.Validation("inventory.item_inactive", "That item is deactivated and cannot be moved.");

    public static readonly Error LocationNotFound =
        Error.NotFound("inventory.location_not_found", "No location with that identifier or code.");

    public static Error LocationBlocked(string code) =>
        Error.Validation("inventory.location_blocked", $"Location {code} is blocked and cannot receive stock.");

    public static readonly Error LotRequired =
        Error.Validation("inventory.lot_required", "This item is lot-tracked, so a lot number is required.");

    public static readonly Error LotNotPermitted =
        Error.Validation("inventory.lot_not_permitted", "This item is not lot-tracked, so it cannot carry a lot.");

    public static readonly Error LotNotFound =
        Error.NotFound("inventory.lot_not_found", "No such lot for this item.");

    public static readonly Error ExpiryRequiresLot =
        Error.Validation("inventory.expiry_requires_lot", "An expiry date belongs to a batch, so it requires a lot number.");

    public static readonly Error QuantityNotPositive =
        Error.Validation("inventory.quantity_not_positive", "Quantity must be greater than zero.");

    public static readonly Error HoldingsIdentical =
        Error.Validation("inventory.holdings_identical", "A movement must change location, licence plate or status.");

    public static Error InsufficientStock(string locationCode, decimal available, decimal requested) =>
        Error.Conflict(
            "inventory.insufficient_stock",
            $"Location {locationCode} holds {available} but {requested} was requested.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("inventory.idempotency_key_required", "Every write command must carry an idempotency key.");

    public static readonly Error IdempotencyKeyReused =
        Error.Conflict(
            "inventory.idempotency_key_reused",
            "That idempotency key was already used for a different movement.");

    public static readonly Error IdempotencyKeysNotDistinct =
        Error.Validation(
            "inventory.idempotency_keys_not_distinct",
            "Every entry in a batch needs its own idempotency key.");

    public static readonly Error NothingToPost =
        Error.Validation("inventory.nothing_to_post", "A posting must carry at least one movement.");

    public static readonly Error LicensePlateEmpty =
        Error.Conflict(
            "inventory.license_plate_empty",
            "That licence plate holds no stock at the location it was scanned in.");

    public static Error InvalidCounterparty(string detail) =>
        Error.Validation("inventory.invalid_counterparty", detail);

    public static readonly Error StatusChangeNeedsNewStatus =
        Error.Validation(
            "inventory.status_change_needs_new_status",
            "A status change must move stock to a different status.");

    public static readonly Error MovementNotFound =
        Error.NotFound("inventory.movement_not_found", "No movement with that identifier.");

    public static readonly Error AlreadyReversed =
        Error.Conflict("inventory.already_reversed", "That movement has already been reversed.");

    public static readonly Error CannotReverseAReversal =
        Error.Validation(
            "inventory.cannot_reverse_a_reversal",
            "Reverse the original entry, not the correction that already undid it.");

    public static readonly Error InvalidSscc =
        Error.Validation("inventory.invalid_sscc", "An SSCC is 18 digits and must satisfy its check digit.");

    public static readonly Error LicensePlateNotFound =
        Error.NotFound("inventory.license_plate_not_found", "No licence plate with that SSCC.");

    public static readonly Error LicensePlateCycle =
        Error.Validation("inventory.license_plate_cycle", "A licence plate cannot be nested inside itself.");

    public static readonly Error AsOfInFuture =
        Error.Validation("inventory.as_of_in_future", "A point-in-time enquiry cannot be in the future.");

    public static readonly Error UnknownActor =
        Error.Unauthorized("inventory.unknown_actor", "The access token carries no usable user identity.");

    // ---- Commitments ----

    public static readonly Error HoldingNotFound =
        Error.NotFound("inventory.holding_not_found", "No stock holding with that identifier.");

    /// <summary>
    /// There is stock, but it is spoken for. Deliberately distinct from
    /// <see cref="InsufficientStock"/>: "the bin is empty" and "the bin is full of
    /// somebody else's order" lead to entirely different conversations, and a
    /// system that reports them with one message sends people to count a shelf
    /// that is exactly as full as it says.
    /// </summary>
    public static Error InsufficientAvailableStock(decimal available, decimal requested) =>
        Error.Conflict(
            "inventory.insufficient_available_stock",
            $"{available} is available to promise but {requested} was requested.");

    /// <summary>
    /// The claim behind a pick was already picked or released. Against an
    /// append-only ledger this is the difference between moving stock once and
    /// moving it twice, so it fails the posting rather than proceeding without a
    /// commitment.
    /// </summary>
    public static readonly Error ReservationNotOpen =
        Error.Conflict(
            "inventory.reservation_not_open",
            "That reservation has already been picked or released.");

    public static readonly Error ReservationNotFound =
        Error.NotFound("inventory.reservation_not_found", "No reservation with that identifier.");

    public static readonly Error NothingToDespatch =
        Error.Validation("inventory.nothing_to_despatch", "A despatch must carry at least one line.");

    // ---- Counting ----

    /// <summary>
    /// Deliberately not <see cref="QuantityNotPositive"/>. Zero is a perfectly good
    /// count — the operator found an empty shelf, which is often the whole point of
    /// sending them — so only a negative reading is refused, and it is refused as
    /// the typing slip it is rather than as a business rule.
    /// </summary>
    public static readonly Error CountedQuantityNegative =
        Error.Validation("inventory.counted_quantity_negative", "A count cannot find less than nothing.");

    public static Error CannotCountCounterparty(string code) =>
        Error.Validation(
            "inventory.cannot_count_counterparty",
            $"{code} is a counterparty account rather than a place, so nobody can stand in front of it and count it.");

    public static readonly Error ScrapNotesRequired =
        Error.Validation(
            "inventory.scrap_notes_required",
            "A write-off must say why. Every other movement is explained by its document; this one is not.");
}
