using Countflow.Modules.Labels.Binding;

namespace Countflow.Modules.Labels.Kinds;

/// <summary>
/// The pallet label's binding contract: every fact a pallet template may print,
/// and nothing else.
/// <para>
/// This list is the code half of <see cref="Domain.LabelKind"/>. Adding a field
/// here is a change to this repository, and that is the price of a template that
/// can be checked before it is used — the same price <c>PutawayRuleCatalogue</c>
/// pays so that a rule set can be validated by building it.
/// </para>
/// </summary>
public static class PalletLabelFields
{
    public const string Sscc = "pallet.sscc";
    public const string SsccHumanReadable = "pallet.sscc_formatted";
    public const string WarehouseCode = "pallet.warehouse_code";
    public const string WarehouseName = "pallet.warehouse_name";
    public const string PrintedAt = "pallet.printed_at";

    public const string Sku = "pallet.sku";
    public const string Description = "pallet.description";
    public const string LotNumber = "pallet.lot_number";
    public const string ExpiryDate = "pallet.expiry_date";
    public const string Quantity = "pallet.quantity";
    public const string BaseUom = "pallet.base_uom";
    public const string CaseCount = "pallet.case_count";
    public const string CaseGtin = "pallet.case_gtin";
    public const string LocationCode = "pallet.location_code";

    /// <summary>
    /// <c>mixed</c>, <c>single</c> or <c>empty</c>. A template prints it as a
    /// caption so that a pallet carrying several SKUs says so in words, rather than
    /// being a pallet label with a suspiciously blank middle.
    /// </summary>
    public const string Content = "pallet.content";

    /// <summary>
    /// Ordered as the editor should list them: identity first, then contents.
    /// <para>
    /// Only the first five are always present. Everything below them describes what
    /// is <i>on</i> the pallet, and a pallet that has been labelled and not yet
    /// built has nothing on it — which is the ordinary state of a label between
    /// coming off the printer and being stuck to something.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LabelFieldDefinition> All { get; } =
    [
        new(Sscc, "SSCC", true),
        new(SsccHumanReadable, "SSCC, spaced for reading", true),
        new(WarehouseCode, "Warehouse code", true),
        new(WarehouseName, "Warehouse name", true),
        new(PrintedAt, "Printed at", true),
        new(Content, "Pallet contents (single, mixed or empty)", true),
        new(Sku, "SKU", false),
        new(Description, "Item description", false),
        new(LotNumber, "Batch or lot number", false),
        new(ExpiryDate, "Expiry date", false),
        new(Quantity, "Quantity in base units", false),
        new(BaseUom, "Base unit of measure", false),
        new(CaseCount, "Number of cases", false),
        new(CaseGtin, "GTIN of the cases on the pallet", false),
        new(LocationCode, "Location", false)
    ];

    public static IReadOnlySet<string> Codes { get; } =
        All.Select(field => field.Code).ToHashSet(StringComparer.Ordinal);
}
