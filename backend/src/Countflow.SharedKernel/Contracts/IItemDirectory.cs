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
    decimal? UnitWeightKg);

/// <summary>MasterData's published read surface for items. See <see cref="ILocationDirectory"/>.</summary>
public interface IItemDirectory
{
    Task<ItemSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ItemSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ItemSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);
}
