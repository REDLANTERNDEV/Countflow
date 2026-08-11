namespace Countflow.Modules.Outbound.Domain;

public enum WaveStatus
{
    /// <summary>Grouped, not yet released. Orders can still be added or taken out.</summary>
    Planned = 1,

    /// <summary>Released. Every order in it has been allocated and its picks are in the queue.</summary>
    Released = 2,

    Cancelled = 3
}

/// <summary>
/// A batch of orders released together.
/// <para>
/// The reason waves exist is travel. Releasing forty orders one at a time sends
/// forty pickers down the same aisle; releasing them together lets the queue hand
/// out the work in route order, which is the single largest lever on outbound
/// labour in a warehouse this size.
/// </para>
/// <para>
/// What this is <b>not</b>, yet, is a decision. <c>DecisionKind.Waving</c> names
/// the rule set that would choose which orders belong together — by carrier
/// cut-off, by zone, by pick density — and it has no vocabulary. A wave here is a
/// grouping somebody made, released through the same path a single order takes.
/// Giving it rules is Phase 6's kind of work, because the thing that makes a
/// waving rule set worth having is being able to replay it against a month of
/// real orders and see whether it actually saved anybody a walk.
/// </para>
/// </summary>
public sealed class Wave
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    public required string Reference { get; set; }

    public WaveStatus Status { get; set; } = WaveStatus.Planned;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ReleasedAt { get; set; }

    public string? ReleasedByUserName { get; set; }
}
