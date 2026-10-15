namespace Countflow.Modules.Inventory.Domain;

/// <summary>Holding-level aging, shared by the live projection and historical fold.</summary>
public static class ReceiptAge
{
    public static DateTimeOffset? Incoming(MovementType type, DateTimeOffset occurredAt, DateTimeOffset? source) => type switch
    {
        MovementType.Receipt => occurredAt,
        MovementType.Adjustment or MovementType.CountAdjustment => null,
        _ => source
    };

    public static DateTimeOffset? After(decimal quantity, DateTimeOffset? current, decimal delta, DateTimeOffset? incoming)
    {
        if (quantity + delta <= 0) return null;
        if (delta <= 0) return current;
        if (quantity <= 0) return incoming;
        // Unknown-origin units cannot acquire a fictitious receipt date by mixing with known stock.
        return current is { } first && incoming is { } next ? (first <= next ? first : next) : null;
    }
}
