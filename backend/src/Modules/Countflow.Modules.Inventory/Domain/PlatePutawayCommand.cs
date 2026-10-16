namespace Countflow.Modules.Inventory.Domain;

/// <summary>A terminal command result, committed with its movements or refusal.</summary>
public sealed class PlatePutawayCommand
{
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public Guid LicensePlateId { get; init; }
    public Guid WorkTaskId { get; init; }
    public DateTimeOffset PreparedAt { get; init; }
    public string? OutcomeJson { get; set; }
}
