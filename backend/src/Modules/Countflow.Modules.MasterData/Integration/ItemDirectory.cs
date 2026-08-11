using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Integration;

/// <summary>
/// MasterData's published read surface for items (ADR 0002) — the only way
/// another module learns anything about a SKU.
/// <para>
/// Every query is <c>AsNoTracking</c> and returns a snapshot record. That is not
/// a performance tweak: handing back a tracked <see cref="Item"/> would give the
/// caller a live handle on this module's tables, which is precisely what the
/// boundary exists to prevent.
/// </para>
/// </summary>
public sealed class ItemDirectory(MasterDataDbContext dbContext) : IItemDirectory
{
    public async Task<ItemSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        ToSnapshot(await AsRows(dbContext.Items.Where(i => i.Id == id))
            .SingleOrDefaultAsync(cancellationToken));

    public async Task<ItemSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
        ToSnapshot(await AsRows(dbContext.Items.Where(i => i.Sku == sku))
            .SingleOrDefaultAsync(cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, ItemSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ItemSnapshot>();
        }

        var distinct = ids.Distinct().ToArray();

        var rows = await AsRows(dbContext.Items.Where(i => distinct.Contains(i.Id)))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Id, r => ToSnapshot(r)!);
    }

    /// <summary>
    /// Reads only the columns the contract exposes, and reads the Each pack
    /// level's envelope alongside them.
    /// <para>
    /// Applied <b>after</b> the predicate, never before: filtering an
    /// already-projected query leaves EF trying to translate a comparison against
    /// a constructor call, which it cannot do.
    /// </para>
    /// <para>
    /// Mapped to the contract type in memory rather than in the <c>Select</c>,
    /// for the same reason <see cref="LocationDirectory"/> does it: the enum
    /// translation is a method call EF has no SQL for, and duplicating it as a
    /// <c>CASE</c> would create a second definition to keep in step.
    /// </para>
    /// </summary>
    private static IQueryable<ItemRow> AsRows(IQueryable<Item> items) =>
        items
            .AsNoTracking()
            .Select(i => new ItemRow(
                i.Id,
                i.Sku,
                i.Description,
                i.BaseUom,
                i.IsLotTracked,
                i.IsExpiryTracked,
                i.IsSerialTracked,
                i.IsActive,
                i.AbcClass,
                i.TemperatureClass,
                i.IsHazardous,
                i.ShelfLifeDays,
                i.MinimumRemainingShelfLifePercent,
                i.PackConfigurations
                    .Where(p => p.Level == PackagingLevel.Each)
                    .Select(p => new PackRow(p.BaseUnitQuantity, p.LengthMm, p.WidthMm, p.HeightMm, p.GrossWeightKg))
                    .FirstOrDefault()));

    private sealed record PackRow(
        int BaseUnitQuantity,
        decimal? LengthMm,
        decimal? WidthMm,
        decimal? HeightMm,
        decimal? GrossWeightKg);

    private sealed record ItemRow(
        Guid Id,
        string Sku,
        string Description,
        string BaseUom,
        bool IsLotTracked,
        bool IsExpiryTracked,
        bool IsSerialTracked,
        bool IsActive,
        AbcClass AbcClass,
        TemperatureClass TemperatureClass,
        bool IsHazardous,
        int? ShelfLifeDays,
        int? MinimumRemainingShelfLifePercent,
        PackRow? Each);

    private static ItemSnapshot? ToSnapshot(ItemRow? row) => row is null
        ? null
        : new ItemSnapshot(
            row.Id,
            row.Sku,
            row.Description,
            row.BaseUom,
            row.IsLotTracked,
            row.IsExpiryTracked,
            row.IsSerialTracked,
            row.IsActive,
            ToBand(row.AbcClass),
            ToBand(row.TemperatureClass),
            row.IsHazardous,
            UnitVolume(row.Each),
            UnitWeight(row.Each),
            row.ShelfLifeDays,
            row.MinimumRemainingShelfLifePercent);

    /// <summary>
    /// Volume of one base unit. Null unless every dimension is on file — a
    /// capacity rule must be able to tell "no data" from "takes no space".
    /// </summary>
    private static decimal? UnitVolume(PackRow? each) =>
        each is { BaseUnitQuantity: > 0, LengthMm: { } length, WidthMm: { } width, HeightMm: { } height }
            ? length * width * height / 1_000_000_000m / each.BaseUnitQuantity
            : null;

    private static decimal? UnitWeight(PackRow? each) =>
        each is { BaseUnitQuantity: > 0, GrossWeightKg: { } weight }
            ? weight / each.BaseUnitQuantity
            : null;

    private static AbcBand ToBand(AbcClass abcClass) => abcClass switch
    {
        AbcClass.A => AbcBand.A,
        AbcClass.B => AbcBand.B,
        _ => AbcBand.C
    };

    private static TemperatureBand ToBand(TemperatureClass temperature) => temperature switch
    {
        TemperatureClass.Chilled => TemperatureBand.Chilled,
        TemperatureClass.Frozen => TemperatureBand.Frozen,
        _ => TemperatureBand.Ambient
    };
}
