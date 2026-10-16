namespace Countflow.Modules.Outbound.Domain;

/// <summary>A durable, supervisor-requested replacement of one line's stranded commitments.</summary>
public sealed class OrderLineRepair
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid SalesOrderLineId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public required string RequestedByUserName { get; init; }
    public required Guid RequestedByUserId { get; init; }
    public required Guid[] AllocationIds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool WithdrawalComplete { get; set; }
    public string? AllocationPlanJson { get; set; }
    public int ReservationsReleased { get; set; }
    public int TasksCancelled { get; set; }
    public int AllocationsRaised { get; set; }
    public int TasksRaised { get; set; }
    public decimal ShortfallQuantity { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Rejects stale Outbound saves after a replacement lease has taken over.</summary>
public sealed class OrderMutationFence
{
    public Guid OrderId { get; init; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
