namespace Countflow.Modules.Counting.Domain;

/// <summary>Opt-in warehouse cadence and an unfinished planning identity.</summary>
public sealed class CountSchedule
{
    public Guid WarehouseId { get; init; }
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 1440;
    public int MaxTasks { get; set; } = 20;
    public DateTimeOffset? NextRunAt { get; set; }
    public Guid? PendingRunId { get; set; }
    public DateTimeOffset? PendingScheduledAt { get; set; }
    public Guid? LastPlanId { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid Revision { get; set; } = Guid.CreateVersion7();
}
