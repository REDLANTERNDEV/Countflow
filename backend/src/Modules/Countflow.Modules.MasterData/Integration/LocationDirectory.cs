using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Integration;

/// <summary>
/// MasterData's published read surface for locations (ADR 0002). See
/// <see cref="ItemDirectory"/> for why these return snapshots rather than
/// entities.
/// </summary>
public sealed class LocationDirectory(MasterDataDbContext dbContext) : ILocationDirectory
{
    public async Task<LocationSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        ToSnapshot(await AsRows(dbContext.Locations.Where(l => l.Id == id))
            .SingleOrDefaultAsync(cancellationToken));

    public async Task<LocationSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        ToSnapshot(await AsRows(dbContext.Locations.Where(l => l.Code == code))
            .SingleOrDefaultAsync(cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, LocationSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, LocationSnapshot>();
        }

        var distinct = ids.Distinct().ToArray();

        var rows = await AsRows(dbContext.Locations.Where(l => distinct.Contains(l.Id)))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(l => l.Id, l => ToSnapshot(l)!);
    }

    public async Task<LocationSnapshot?> FindCounterpartyAsync(
        Guid warehouseId,
        CounterpartyKind kind,
        CancellationToken cancellationToken)
    {
        if (ToLocationType(kind) is not { } type)
        {
            // CounterpartyKind.None is not a location: asking for "the physical
            // counterparty" is a caller bug, not an empty result.
            return null;
        }

        return ToSnapshot(await AsRows(
                dbContext.Locations.Where(l => l.WarehouseId == warehouseId && l.Type == type))
            .SingleOrDefaultAsync(cancellationToken));
    }

    public async Task<StorageLocationSnapshot?> FindStorageLocationByCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var row = await AsStorageRows(dbContext.Locations.Where(l => l.Code == code))
            .SingleOrDefaultAsync(cancellationToken);

        return row is null || VirtualTypes.Contains(row.Type) ? null : ToStorageSnapshot(row);
    }

    public async Task<IReadOnlyDictionary<Guid, StorageLocationSnapshot>> FindStorageLocationsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, StorageLocationSnapshot>();
        }

        var distinct = ids.Distinct().ToArray();

        var rows = await AsStorageRows(dbContext.Locations.Where(l => distinct.Contains(l.Id)))
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => !VirtualTypes.Contains(row.Type))
            .ToDictionary(row => row.Id, ToStorageSnapshot);
    }

    public async Task<IReadOnlyList<StorageLocationSnapshot>> FindStorageCandidatesAsync(
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        var rows = await AsStorageRows(
                dbContext.Locations.Where(l => l.WarehouseId == warehouseId && !VirtualTypes.Contains(l.Type)))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToStorageSnapshot)];
    }

    /// <summary>
    /// The zone is joined rather than looked up per location: its temperature
    /// class is the item's first hard constraint and its pick sequence sorts ahead
    /// of the location's, so every candidate needs both and a query per candidate
    /// would be two thousand round trips.
    /// </summary>
    private static IQueryable<StorageRow> AsStorageRows(IQueryable<Location> locations) =>
        locations
            .AsNoTracking()
            .Select(l => new StorageRow(
                l.Id,
                l.WarehouseId,
                l.Code,
                l.ZoneId,
                l.Zone == null ? null : l.Zone.Code,
                l.Zone == null ? TemperatureClass.Ambient : l.Zone.TemperatureClass,
                l.Type,
                l.IsActive,
                l.IsPickable,
                l.IsReplenishable,
                l.Zone == null ? 0 : l.Zone.PickSequence,
                l.PickSequence,
                l.MaxVolumeM3,
                l.MaxWeightKg,
                l.MaxDistinctItems));

    private static StorageLocationSnapshot ToStorageSnapshot(StorageRow row) =>
        new(
            row.Id,
            row.WarehouseId,
            row.Code,
            row.ZoneId,
            row.ZoneCode,
            ToBand(row.Temperature),
            ToRole(row.Type),
            row.IsActive,
            row.IsPickable,
            row.IsReplenishable,
            row.ZonePickSequence,
            row.PickSequence,
            row.MaxVolumeM3,
            row.MaxWeightKg,
            row.MaxDistinctItems);

    /// <summary>
    /// Spelled out as data rather than as <c>!l.Type.IsVirtual()</c>, because the
    /// extension method is a C# call EF cannot translate and the alternative —
    /// fetching all two thousand locations and filtering in memory — is worse.
    /// It is therefore a second copy of a definition that lives in
    /// <see cref="LocationTypeExtensions.IsVirtual"/>, and the two are held in
    /// step by test rather than by hope.
    /// </summary>
    private static readonly LocationType[] VirtualTypes =
    [
        LocationType.Supplier,
        LocationType.Customer,
        LocationType.Adjustment,
        LocationType.Scrap,
        LocationType.InTransit
    ];

    private sealed record StorageRow(
        Guid Id,
        Guid WarehouseId,
        string Code,
        Guid? ZoneId,
        string? ZoneCode,
        TemperatureClass Temperature,
        LocationType Type,
        bool IsActive,
        bool IsPickable,
        bool IsReplenishable,
        int ZonePickSequence,
        int PickSequence,
        decimal? MaxVolumeM3,
        decimal? MaxWeightKg,
        int? MaxDistinctItems);

    private static TemperatureBand ToBand(TemperatureClass temperature) => temperature switch
    {
        TemperatureClass.Chilled => TemperatureBand.Chilled,
        TemperatureClass.Frozen => TemperatureBand.Frozen,
        _ => TemperatureBand.Ambient
    };

    private static StorageRole ToRole(LocationType type) => type switch
    {
        LocationType.Bulk => StorageRole.Reserve,
        LocationType.PickFace => StorageRole.Forward,
        LocationType.Staging => StorageRole.Staging,
        LocationType.Quarantine => StorageRole.Hold,
        LocationType.ReceivingDock or LocationType.ShippingDock => StorageRole.Dock,
        LocationType.Packing => StorageRole.Bench,
        _ => StorageRole.None
    };

    /// <summary>
    /// Reads only the columns the contract exposes, then maps in memory so
    /// <see cref="LocationTypeExtensions"/> stays the single definition of what a
    /// counterparty is — duplicating that logic as a SQL CASE is how the two
    /// copies eventually disagree.
    /// <para>
    /// Applied <b>after</b> the predicate, never before: filtering an
    /// already-projected query leaves EF trying to translate a comparison against
    /// a constructor call, which it cannot do.
    /// </para>
    /// </summary>
    private static IQueryable<LocationRow> AsRows(IQueryable<Location> locations) =>
        locations
            .AsNoTracking()
            .Select(l => new LocationRow(l.Id, l.WarehouseId, l.Code, l.Type, l.IsActive));

    private sealed record LocationRow(Guid Id, Guid WarehouseId, string Code, LocationType Type, bool IsActive);

    private static LocationSnapshot? ToSnapshot(LocationRow? row) => row is null
        ? null
        : new LocationSnapshot(
            row.Id,
            row.WarehouseId,
            row.Code,
            ToCounterparty(row.Type),
            row.IsActive,
            row.Type.CanHoldAvailableStock());

    private static CounterpartyKind ToCounterparty(LocationType type) => type switch
    {
        LocationType.Supplier => CounterpartyKind.Supplier,
        LocationType.Customer => CounterpartyKind.Customer,
        LocationType.Adjustment => CounterpartyKind.Adjustment,
        LocationType.Scrap => CounterpartyKind.Scrap,
        LocationType.InTransit => CounterpartyKind.InTransit,
        _ => CounterpartyKind.None
    };

    private static LocationType? ToLocationType(CounterpartyKind kind) => kind switch
    {
        CounterpartyKind.Supplier => LocationType.Supplier,
        CounterpartyKind.Customer => LocationType.Customer,
        CounterpartyKind.Adjustment => LocationType.Adjustment,
        CounterpartyKind.Scrap => LocationType.Scrap,
        CounterpartyKind.InTransit => LocationType.InTransit,
        _ => null
    };
}
