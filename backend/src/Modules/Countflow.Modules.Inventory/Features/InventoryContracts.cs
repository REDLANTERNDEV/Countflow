using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;

namespace Countflow.Modules.Inventory.Features;

/// <summary>
/// What a caller gets back from any command that posts to the ledger.
/// <para>
/// Deliberately includes both resulting balances. A handheld that has just moved
/// a case wants to show the operator what is left in the bin they took it from —
/// without it, every confirmation screen needs a second round trip, over the
/// least reliable link in the building.
/// </para>
/// </summary>
public sealed record MovementReceiptResponse(
    Guid MovementId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Sku,
    decimal Quantity,
    string FromLocationCode,
    string ToLocationCode,
    InventoryStatus FromStatus,
    InventoryStatus ToStatus,
    string? LotNumber,
    string? Sscc,
    decimal FromQuantityAfter,
    decimal ToQuantityAfter,
    bool WasAlreadyPosted)
{
    public static MovementReceiptResponse From(
        MovementReceipt receipt,
        string sku,
        string fromLocationCode,
        string toLocationCode,
        InventoryStatus fromStatus,
        InventoryStatus toStatus,
        string? lotNumber,
        string? sscc) =>
        new(
            receipt.MovementId,
            receipt.Sequence,
            receipt.OccurredAt,
            sku,
            receipt.Quantity,
            fromLocationCode,
            toLocationCode,
            fromStatus,
            toStatus,
            lotNumber,
            sscc,
            receipt.FromQuantityAfter,
            receipt.ToQuantityAfter,
            receipt.WasAlreadyPosted);
}

/// <summary>A page of results. Mirrors the master-data shape so clients handle one envelope.</summary>
public sealed record InventoryPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
