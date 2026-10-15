namespace Countflow.Modules.Counting.Domain;

/// <summary>Fences warehouse orchestration writes after advisory-session loss.</summary>
public sealed class CountPlanningFence
{
    public Guid WarehouseId { get; init; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
