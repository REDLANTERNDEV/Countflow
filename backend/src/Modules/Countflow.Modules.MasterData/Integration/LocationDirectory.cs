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
