namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// What kind of operation a ledger entry records.
/// <para>
/// This is the movement's *shape*, distinct from <see cref="MovementReason"/>,
/// which is why it happened. A receipt is always a movement from a Supplier
/// counterparty into the building; the reason distinguishes a purchase-order
/// receipt from a customer return. Keeping the two apart is what lets the ledger
/// insist on the shape while leaving the business vocabulary open to extension.
/// </para>
/// </summary>
public enum MovementType
{
    /// <summary>Goods enter the building. From a Supplier counterparty.</summary>
    Receipt = 1,

    /// <summary>Dock to a storage location.</summary>
    Putaway = 2,

    /// <summary>Any location-to-location move inside the building.</summary>
    Transfer = 3,

    /// <summary>Reserve to pick face.</summary>
    Replenishment = 4,

    /// <summary>Storage to a pack or staging location, against an order line.</summary>
    Pick = 5,

    /// <summary>Goods leave the building. To a Customer counterparty.</summary>
    Despatch = 6,

    /// <summary>Quantity correction against the Adjustment counterparty.</summary>
    Adjustment = 7,

    /// <summary>Count variance, from a cycle count or a full stocktake.</summary>
    CountAdjustment = 8,

    /// <summary>Same location and quantity, different status. Quarantine, release, write-off to damaged.</summary>
    StatusChange = 9,

    /// <summary>Destruction or write-off, to the Scrap counterparty.</summary>
    Scrap = 10,

    /// <summary>
    /// The exact mirror of an earlier entry. The only sanctioned way to undo
    /// something, because the ledger is append-only: a correction adds a row, it
    /// never removes one.
    /// </summary>
    Reversal = 11
}
