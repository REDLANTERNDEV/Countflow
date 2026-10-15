namespace Countflow.Modules.Inventory.Domain;

/// <summary>One-time completion marker for rebuilding receipt age from the existing ledger.</summary>
public sealed class ReceiptAgeProjectionState
{
    public int Id { get; init; } = 1;
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
