using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Domain;

/// <summary>
/// Where a rule set is in its life.
/// <para>
/// Three states rather than an <c>IsActive</c> flag, because promotion is the
/// interesting event. A draft can be edited and replayed against history; an
/// active set is what the floor is running on; an archived one is what a trace
/// from last quarter still points at and must therefore never be deleted.
/// </para>
/// </summary>
public enum RuleSetStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3
}

/// <summary>
/// A named, versioned collection of rules for one kind of decision (ADR 0004).
/// <para>
/// Data, not code. Changing which bin a pallet goes to is a matter of editing
/// rows and promoting a version, not of shipping a release — that is what makes
/// it possible to tune a warehouse at the speed a warehouse actually changes.
/// The rule <i>implementations</i> are code, and deliberately so; see
/// <see cref="RuleDefinition.Code"/>.
/// </para>
/// </summary>
public sealed class RuleSet
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DecisionKind Kind { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// Increments per promotion. Stamped onto every trace, so a decision made
    /// last month is still explicable by the rules that actually made it.
    /// </summary>
    public required int Version { get; set; }

    public RuleSetStatus Status { get; set; } = RuleSetStatus.Draft;

    /// <summary>
    /// Null means "every warehouse". A site with genuinely different physics —
    /// a cold store, a hazardous-goods depot — gets its own set, and the
    /// warehouse-specific one wins over the default.
    /// </summary>
    public Guid? WarehouseId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ActivatedAt { get; set; }

    public ICollection<RuleDefinition> Rules { get; init; } = [];
}
