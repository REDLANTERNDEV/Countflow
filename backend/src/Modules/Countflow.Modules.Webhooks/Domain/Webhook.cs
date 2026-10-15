namespace Countflow.Modules.Webhooks.Domain;

public sealed class Subscription
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Url { get; init; }
    public required string ProtectedSecret { get; init; }
    public bool Verified { get; set; }
    public bool Enabled { get; set; }
    public long Cursor { get; set; }
    public Guid? PendingDeliveryId { get; set; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
public enum DeliveryState { Pending, Delivered, DeadLetter }
public sealed class Delivery
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid SubscriptionId { get; init; }
    public required string Body { get; init; }
    public long NextCursor { get; init; }
    public int Attempts { get; set; }
    public DeliveryState State { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int? LastStatus { get; set; }
    public string? LastErrorCode { get; set; }
}
public sealed class InboundTrigger
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public bool Enabled { get; set; } = true;
}
public sealed class TriggerRequest
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TriggerId { get; init; }
    public required string RetryKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
