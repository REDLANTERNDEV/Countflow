using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Labels;

/// <summary>
/// This module's coded failures.
/// <para>
/// Most of them are reported at the moment somebody saves a template rather than
/// at the moment somebody prints, and that is the point of the design: a template
/// is checked by rendering it, so a layout that cannot print is refused in the
/// editor rather than at a rack with a pallet waiting. Only the last group here
/// can reach a print.
/// </para>
/// </summary>
public static class LabelsErrors
{
    public static readonly Error TemplateNotFound =
        Error.NotFound("labels.template_not_found", "No label template with that identifier.");

    public static readonly Error WarehouseNotFound =
        Error.NotFound("labels.warehouse_not_found", "No warehouse with that code.");

    public static readonly Error ItemNotFound =
        Error.NotFound("labels.item_not_found", "The stock on that pallet names an item the catalogue does not have.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("labels.idempotency_key_required", "Every write command must carry an idempotency key.");

    public static readonly Error InvalidSscc =
        Error.Validation("labels.invalid_sscc", "That is not a valid SSCC — the check digit does not hold.");

    /// <summary>
    /// Only a draft may be edited. Same rule a rule set follows, and for a reason
    /// this module has of its own: an active template is what the floor is printing
    /// and an archived one is what a <c>LabelPrint</c> from last quarter points at,
    /// so editing either would change the account of a physical label that is
    /// already stuck to something.
    /// </summary>
    public static readonly Error TemplateNotEditable =
        Error.Conflict("labels.template_not_editable", "Only a draft template can be edited. Clone it, edit the copy, and promote that.");

    public static readonly Error TemplateNotPromotable =
        Error.Conflict("labels.template_not_promotable", "Only a draft template can be promoted.");

    public static readonly Error TemplateEmpty =
        Error.Validation("labels.template_empty", "A template with no elements would print a blank label.");

    public static readonly Error TemplateHasNoExtent =
        Error.Validation("labels.template_has_no_extent", "A template needs a width and a height in dots.");

    public static readonly Error BindingKindMismatch =
        Error.Failure("labels.binding_kind_mismatch", "A template was rendered against a binding for a different label kind.");

    /// <summary>
    /// No active template for this kind and site. A conflict rather than a
    /// not-found: the request named something that exists, and what is missing is a
    /// promotion somebody has not done.
    /// </summary>
    public static Error NoActiveTemplate(string kind) =>
        Error.Conflict(
            "labels.no_active_template",
            $"No active {kind} label template for that warehouse. Promote one before printing.");

    public static Error NoVocabulary(string kind) =>
        Error.Validation("labels.no_vocabulary", $"There is no field vocabulary for {kind} labels.");

    public static Error UnknownField(string code, string kind) =>
        Error.Validation(
            "labels.unknown_field",
            $"'{code}' is not a field a {kind} label publishes. An unknown field is refused rather than printed blank.");

    public static Error DuplicateElementOrder(int order) =>
        Error.Validation("labels.duplicate_element_order", $"Two elements both claim draw order {order}.");

    public static Error ElementOffLabel(int order) =>
        Error.Validation("labels.element_off_label", $"Element {order} starts outside the label's extent, so nothing of it would print.");

    public static Error InvalidRotation(string rotation) =>
        Error.Validation("labels.invalid_rotation", $"'{rotation}' is not a ZPL rotation. Use N, R, I or B.");

    public static Error UnknownElementType(string type) =>
        Error.Validation("labels.unknown_element_type", $"'{type}' is not an element type this renderer knows.");

    public static Error TextIsBothLiteralAndField(int order) =>
        Error.Validation("labels.text_is_both", $"Element {order} carries both a literal and a field. It prints one thing, so it names one thing.");

    public static Error TextHasNothingToPrint(int order) =>
        Error.Validation("labels.text_has_nothing", $"Element {order} is text with neither a literal nor a field behind it.");

    public static Error BarcodeHasNoSymbology(int order) =>
        Error.Validation("labels.barcode_has_no_symbology", $"Element {order} is a barcode with no symbology.");

    public static Error BarcodeHasNoField(int order) =>
        Error.Validation("labels.barcode_has_no_field", $"Element {order} is a Code 128 barcode with no field to encode.");

    public static Error BarcodeFieldEmpty(string code) =>
        Error.Conflict(
            "labels.barcode_field_empty",
            $"'{code}' has no value on this subject, and a barcode encoding nothing scans as nothing while looking like a barcode.");

    public static Error BoxHasNoExtent(int order) =>
        Error.Validation("labels.box_has_no_extent", $"Element {order} is a box with neither width nor height.");
}
