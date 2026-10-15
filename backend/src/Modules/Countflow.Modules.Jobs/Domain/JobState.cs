namespace Countflow.Modules.Jobs.Domain;

/// <summary>Scheduling state and the unfinished run's identity, owned only by Jobs.</summary>
public sealed class JobState
{
    public required string Name { get; init; }
    public DateTimeOffset NextRunAt { get; set; }
    public Guid AttemptId { get; set; }
    public Guid? PendingRunId { get; set; }
    public DateTimeOffset? PendingScheduledAt { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastFinishedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastErrorCode { get; set; }
}
