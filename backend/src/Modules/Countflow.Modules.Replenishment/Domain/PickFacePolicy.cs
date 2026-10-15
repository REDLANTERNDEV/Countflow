namespace Countflow.Modules.Replenishment.Domain;

/// <summary>One SKU's forward face, and the minimum/maximum available-stock policy.</summary>
public sealed class PickFacePolicy
{
    public Guid LocationId { get; init; }
    public Guid WarehouseId { get; init; }
    public Guid ItemId { get; set; }
    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; }
    public bool Enabled { get; set; }
    public Guid? PendingPlanId { get; set; }
    public Guid? LastPlanId { get; set; }
    public Guid? LastDecisionTraceId { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public decimal LastShortfall { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
