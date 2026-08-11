namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// A batch of one item, as identified by the manufacturer.
/// <para>
/// Expiry lives here rather than on the balance or the movement because an
/// expiry date is a property of the batch: every unit of lot ABC123 expires on
/// the same day, wherever in the warehouse it sits. Recording it per-holding
/// would let two shelves disagree about when the same batch goes out of date.
/// </para>
/// </summary>
public sealed class Lot
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Owning item. Lot numbers are unique per item, never globally — two suppliers reuse "001" freely.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>As printed on the label, GS1 AI 10. Stored verbatim: it is an identifier, so it is never reformatted.</summary>
    public required string LotNumber { get; init; }

    /// <summary>GS1 AI 17. Drives FEFO rotation and the minimum-remaining-shelf-life rule.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>GS1 AI 11. Used to derive an expiry when the supplier prints a production date instead.</summary>
    public DateOnly? ProductionDate { get; set; }

    /// <summary>The supplier's own batch reference, where it differs from the one on the label.</summary>
    public string? SupplierLotNumber { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
