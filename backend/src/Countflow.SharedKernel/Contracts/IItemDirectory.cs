namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// Velocity band. Mirrors MasterData's <c>AbcClass</c>, published because
/// slotting rules are the whole reason the classification is maintained.
/// </summary>
public enum AbcBand
{
    A = 1,
    B = 2,
    C = 3
}

/// <summary>
/// Storage temperature regime. Mirrors MasterData's <c>TemperatureClass</c>: the
/// first hard constraint on putaway is that these match, so both the item side
/// and the location side of the comparison have to cross the boundary.
/// </summary>
public enum TemperatureBand
{
    Ambient = 1,
    Chilled = 2,
    Frozen = 3
}

/// <summary>
/// What another module is allowed to know about an item.
/// <para>
/// The tracking flags are here because they are policy, not description: the
/// ledger has to refuse a movement of a lot-tracked item that arrives without a
/// lot, and it cannot ask the item catalogue's tables directly.
/// </para>
/// <para>
/// The handling attributes below joined them in Phase 3 for the same reason. A
/// putaway rule set decides where a pallet goes from the item's temperature
/// class, velocity, hazard flag and physical envelope; without them on the
/// snapshot the rules engine would need MasterData's tables, which is exactly
/// the coupling ADR 0002 forbids. They are attributes of the item that other
/// modules must *reason* about, not merely display, which is the line this
/// contract draws.
/// </para>
/// </summary>
/// <param name="UnitVolumeM3">
/// Volume of one base unit, in m³. Null when the item's pack configurations carry
/// no dimensions — common, and the reason capacity constraints have to treat
/// "unknown" as distinct from "zero".
/// </param>
/// <param name="UnitWeightKg">Gross weight of one base unit, in kg. Null for the same reason.</param>
/// <param name="ShelfLifeDays">
/// Total life of a batch from production, in days. Crossed the boundary in Phase 4
/// as the denominator of <paramref name="MinimumRemainingShelfLifePercent"/>: a
/// remaining-life rule that only knew the expiry date could say how long is left
/// but not what fraction of the life that is, and the fraction is what the
/// customer's contract is written in.
/// </param>
/// <param name="MinimumRemainingShelfLifePercent">
/// How much of that life must still be ahead of the goods for a customer to
/// accept them. Null when the item has no such policy, which is most of a
/// non-food catalogue. Read by the allocation rules and by nothing else — stock
/// inside this window is still perfectly sellable, it just cannot be sent to
/// <i>this</i> order, which is why it is an allocation constraint rather than a
/// status.
/// </param>
public sealed record ItemSnapshot(
    Guid Id,
    string Sku,
    string Description,
    string BaseUom,
    bool IsLotTracked,
    bool IsExpiryTracked,
    bool IsSerialTracked,
    bool IsActive,
    AbcBand AbcBand,
    TemperatureBand TemperatureBand,
    bool IsHazardous,
    decimal? UnitVolumeM3,
    decimal? UnitWeightKg,
    int? ShelfLifeDays = null,
    int? MinimumRemainingShelfLifePercent = null);

/// <summary>MasterData's published read surface for items. See <see cref="ILocationDirectory"/>.</summary>
public interface IItemDirectory
{
    Task<ItemSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ItemSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ItemSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);
}
