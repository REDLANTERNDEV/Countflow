namespace Countflow.Modules.Work.Domain;

/// <summary>An appended explanation and the frozen load it evaluated. Never overwritten.</summary>
public sealed class PutawayDecisionRevision
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid WorkTaskId { get; init; }
    public required long Revision { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public Guid? PreviousDecisionTraceId { get; init; }
    public required Guid DecisionTraceId { get; init; }
    public Guid? SuggestedLocationId { get; init; }
    public required string ContentsJson { get; init; }
    public required DateTimeOffset DecidedAt { get; init; }
    public required Guid UserId { get; init; }
    public required string UserName { get; init; }
}
