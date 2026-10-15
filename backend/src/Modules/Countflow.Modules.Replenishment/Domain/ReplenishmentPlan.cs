namespace Countflow.Modules.Replenishment.Domain;

public enum ReplenishmentPlanStatus { Open = 1, Finished = 2, Cancelled = 3 }

/// <summary>A durable intent recorded before its stock claims or tasks are raised.</summary>
public sealed class ReplenishmentPlan
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public long Number { get; set; }
    public string Reference => $"RPL-{Number:D6}";
    public Guid LocationId { get; init; }
    public Guid WarehouseId { get; init; }
    public Guid ItemId { get; init; }
    public Guid DecisionTraceId { get; init; }
    public decimal RequestedQuantity { get; init; }
    public ReplenishmentPlanStatus Status { get; set; } = ReplenishmentPlanStatus.Open;
    public DateTimeOffset CreatedAt { get; init; }
    public List<ReplenishmentLine> Lines { get; init; } = [];
}

public sealed class ReplenishmentLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid PlanId { get; init; }
    public Guid BalanceId { get; init; }
    public Guid FromLocationId { get; init; }
    public Guid? LotId { get; init; }
    public Guid? LicensePlateId { get; init; }
    public decimal Quantity { get; init; }
    public Guid? ReservationId { get; set; }
    public Guid? WorkTaskId { get; set; }
    public bool Refused { get; set; }
    public string? ErrorCode { get; set; }
}
