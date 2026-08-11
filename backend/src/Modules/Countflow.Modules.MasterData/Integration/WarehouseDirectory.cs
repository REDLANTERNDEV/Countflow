using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Integration;

/// <summary>
/// MasterData's published read surface for warehouses (ADR 0002). See
/// <see cref="ItemDirectory"/> for why these return snapshots rather than
/// entities.
/// </summary>
public sealed class WarehouseDirectory(MasterDataDbContext dbContext) : IWarehouseDirectory
{
    public async Task<WarehouseSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await AsSnapshots(dbContext.Warehouses.Where(w => w.Id == id)).SingleOrDefaultAsync(cancellationToken);

    public async Task<WarehouseSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        await AsSnapshots(dbContext.Warehouses.Where(w => w.Code == code)).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WarehouseSnapshot>> ListAsync(CancellationToken cancellationToken) =>
        await AsSnapshots(dbContext.Warehouses.OrderBy(w => w.Code)).ToListAsync(cancellationToken);

    /// <summary>
    /// Applied <b>last</b> — after any predicate and after any ordering, never
    /// before. Filtering or sorting an already-projected query leaves EF trying
    /// to translate <c>new WarehouseSnapshot(…).Code</c>, which it cannot do, and
    /// it fails at runtime rather than at compile time.
    /// </summary>
    private static IQueryable<WarehouseSnapshot> AsSnapshots(IQueryable<Domain.Warehouse> warehouses) =>
        warehouses
            .AsNoTracking()
            .Select(w => new WarehouseSnapshot(w.Id, w.Code, w.Name, w.IsActive));
}
