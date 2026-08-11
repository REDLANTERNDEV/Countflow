namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// The disposition of a quantity of stock.
/// <para>
/// Status is part of the balance key, not a flag on it, and it only ever changes
/// through a movement (ADR 0001). Quarantining a pallet is therefore a ledger
/// entry — same location, different status — and shows up in the audit trail
/// alongside physical moves rather than as a silent column update nobody can
/// account for later.
/// </para>
/// </summary>
public enum InventoryStatus
{
    /// <summary>Sellable and allocatable. The overwhelming majority of stock.</summary>
    Available = 1,

    /// <summary>Held pending a decision — inspection, recall, suspected damage.</summary>
    Quarantine = 2,

    /// <summary>Confirmed damaged. Still counted, because it is still physically there.</summary>
    Damaged = 3,

    /// <summary>Blocked by a commercial or regulatory hold rather than a physical defect.</summary>
    OnHold = 4,

    /// <summary>Past its expiry date. Kept distinct from Damaged so write-off reporting can separate the two.</summary>
    Expired = 5
}

public static class InventoryStatusExtensions
{
    /// <summary>
    /// Whether stock in this status may be allocated to an outbound order.
    /// Everything else is visible in enquiry and counted in stocktakes, but
    /// invisible to allocation.
    /// </summary>
    public static bool IsAllocatable(this InventoryStatus status) => status == InventoryStatus.Available;
}
