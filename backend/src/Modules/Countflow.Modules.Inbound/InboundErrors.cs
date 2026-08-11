using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Inbound;

public static class InboundErrors
{
    public static readonly Error ItemNotFound =
        Error.NotFound("inbound.item_not_found", "No item with that SKU.");

    public static readonly Error LocationNotFound =
        Error.NotFound("inbound.location_not_found", "No location with that code.");

    /// <summary>
    /// Goods have to land somewhere they can be worked. Booking a pallet straight
    /// into racking bypasses the check-and-putaway loop this module exists to run,
    /// so the receipt endpoint insists on a dock or a staging bay.
    /// </summary>
    public static Error NotAReceivingLocation(string code) =>
        Error.Validation(
            "inbound.not_a_receiving_location",
            $"Location {code} is not a dock or staging bay, so goods cannot be booked in there.");

    public static readonly Error AsnNotFound =
        Error.NotFound("inbound.asn_not_found", "No advice note with that reference.");

    public static readonly Error AsnLineNotFound =
        Error.NotFound("inbound.asn_line_not_found", "That advice note has no such line.");

    /// <summary>
    /// The scanned SKU is not on the advice note. Not silently accepted as a blind
    /// receipt: the operator believes they are receiving against a document, and
    /// quietly booking it outside that document is how a delivery reconciles
    /// perfectly while being wrong.
    /// </summary>
    public static Error ItemNotOnAsn(string sku, string reference) =>
        Error.Validation("inbound.item_not_on_asn", $"SKU {sku} is not on advice note {reference}.");

    public static Error AsnNotOpen(string reference) =>
        Error.Conflict("inbound.asn_not_open", $"Advice note {reference} is closed or cancelled.");

    public static readonly Error AsnReferenceTaken =
        Error.Conflict("inbound.asn_reference_taken", "That advice note reference is already in use.");

    public static readonly Error QuantityNotPositive =
        Error.Validation("inbound.quantity_not_positive", "Quantity must be greater than zero.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("inbound.idempotency_key_required", "Every write command must carry an idempotency key.");

    public static readonly Error NoLines =
        Error.Validation("inbound.no_lines", "An advice note needs at least one line.");
}
