namespace Countflow.Modules.Inventory.Domain;

public sealed class BinCountRecord
{
    public required Guid TaskId { get; init; }
    public required Guid LocationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string Outcome { get; init; }
    public required Guid ConfirmedByUserId { get; init; }
    public required string ConfirmedByUserName { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
}
