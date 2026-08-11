namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// A physical handling unit — a pallet, a cage, a tote — identified by its SSCC.
/// <para>
/// An LPN is what makes a move of 40 cases one scan instead of 40. Nestable,
/// because that is how goods actually arrive: cartons on a pallet, pallets in a
/// container, each with its own licence plate.
/// </para>
/// <para>
/// <b>There is deliberately no CurrentLocationId column.</b> Where an LPN is, is
/// the location of the balances posted against it — derived from the ledger like
/// every other inventory fact (ADR 0001). A denormalised column here would be a
/// second source of truth for position, and the first time it disagreed with the
/// balances nobody would be able to say which was right.
/// </para>
/// </summary>
public sealed class LicensePlate
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Eighteen digits, check digit validated on creation. See <c>Sscc</c> in SharedKernel.</summary>
    public required string Sscc { get; init; }

    /// <summary>
    /// The unit this one sits on or in. Null for a top-level pallet.
    /// <para>
    /// Cycles are rejected when the parent is set. Cascading a move down the nest
    /// — moving a pallet and taking its cartons with it — arrives with Phase 3's
    /// putaway task, which is where a composite command has a caller.
    /// </para>
    /// </summary>
    public Guid? ParentLicensePlateId { get; set; }

    /// <summary>
    /// A closed LPN is built and sealed: nothing should be added to it, though
    /// picking from it stays allowed, because that is how a full pallet gets
    /// broken down.
    /// <para>
    /// Recorded but <b>not yet enforced</b> — nothing sets it in Phase 2. The
    /// place it starts mattering is Phase 3's pallet build, which is also the
    /// first code that has a reason to close one.
    /// </para>
    /// </summary>
    public bool IsClosed { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public LicensePlate? Parent { get; init; }

    public ICollection<LicensePlate> Children { get; init; } = [];
}
