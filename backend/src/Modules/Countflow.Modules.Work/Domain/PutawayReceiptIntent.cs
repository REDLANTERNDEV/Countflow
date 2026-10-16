namespace Countflow.Modules.Work.Domain;

/// <summary>Serializes receipt registration and acknowledgement for a pallet.</summary>
public sealed class PutawayReceiptLane
{
    public required string Sscc { get; init; }
    public Guid? LicensePlateId { get; set; }
}

/// <summary>
/// A durable handoff, created before stock is posted. An ambiguous failure stays
/// pending; only the task-source transaction acknowledges it. Inventory remains
/// the owner of what was physically received.
/// </summary>
public sealed class PutawayReceiptIntent
{
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string Sscc { get; init; }
    public required Guid WarehouseId { get; init; }
    public required Guid FromLocationId { get; init; }
    public required DateTimeOffset PreparedAt { get; init; }
    public bool IsPending { get; set; } = true;
    public DateTimeOffset? AcknowledgedAt { get; set; }
}
