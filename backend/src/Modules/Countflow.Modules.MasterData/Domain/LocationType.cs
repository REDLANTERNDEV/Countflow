namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// What a location is for. Drives putaway eligibility, pick routing, and which
/// side of a stock movement a location may legitimately appear on.
/// </summary>
public enum LocationType
{
    // ---- Physical locations ----

    /// <summary>Where a truck unloads. Stock rests here only between receipt and putaway.</summary>
    ReceivingDock = 1,

    /// <summary>Marshalling space — cross-dock, pre-putaway, or pre-load.</summary>
    Staging = 2,

    /// <summary>Reserve storage. Usually pallets, replenishes pick faces.</summary>
    Bulk = 3,

    /// <summary>Forward pick location. Small, fast, replenished from bulk.</summary>
    PickFace = 4,

    /// <summary>Pack bench.</summary>
    Packing = 5,

    /// <summary>Where a truck loads.</summary>
    ShippingDock = 6,

    /// <summary>Held stock — damage, recall, inspection. Not pickable.</summary>
    Quarantine = 7,

    // ---- Virtual locations ----
    //
    // These are what make the double-entry ledger work with one mechanism
    // (ADR 0001). Goods are never created or destroyed: they arrive *from*
    // Supplier and leave *to* Customer, and a count discrepancy moves stock to
    // or from Adjustment. Each is an ordinary row in the locations table.

    /// <summary>Counterparty for goods receipt.</summary>
    Supplier = 100,

    /// <summary>Counterparty for despatch.</summary>
    Customer = 101,

    /// <summary>Counterparty for count variances and manual corrections.</summary>
    Adjustment = 102,

    /// <summary>Counterparty for write-offs and destruction.</summary>
    Scrap = 103,

    /// <summary>Holds stock in flight between two warehouses.</summary>
    InTransit = 104
}

public static class LocationTypeExtensions
{
    /// <summary>
    /// Virtual locations have no physical address and no capacity. They may
    /// appear on one side of a movement but never hold work.
    /// </summary>
    public static bool IsVirtual(this LocationType type) => type switch
    {
        LocationType.Supplier
            or LocationType.Customer
            or LocationType.Adjustment
            or LocationType.Scrap
            or LocationType.InTransit => true,
        _ => false
    };

    /// <summary>Whether stock in this location can normally be allocated to an order.</summary>
    public static bool CanHoldAvailableStock(this LocationType type) => type switch
    {
        LocationType.Bulk or LocationType.PickFace or LocationType.Staging => true,
        _ => false
    };
}
