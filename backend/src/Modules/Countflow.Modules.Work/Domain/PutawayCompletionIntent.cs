namespace Countflow.Modules.Work.Domain;

public enum PutawayCompletionState { Pending, Succeeded, Refused }

/// <summary>The frozen confirmation which must settle before this pallet can change.</summary>
public sealed class PutawayCompletionIntent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid WorkTaskId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string SnapshotJson { get; init; }
    public PutawayCompletionState State { get; set; }
    public DateTimeOffset PreparedAt { get; init; }
    public DateTimeOffset? SettledAt { get; set; }
    public string? ErrorJson { get; set; }
}
