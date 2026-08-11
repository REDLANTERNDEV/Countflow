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
        await AsSnapshots(dbContext.Items.Where(i => i.Id == id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ItemSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
        await AsSnapshots(dbContext.Items.Where(i => i.Sku == sku))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ItemSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ItemSnapshot>();
        }

        var distinct = ids.Distinct().ToArray();

        return await AsSnapshots(dbContext.Items.Where(i => distinct.Contains(i.Id)))
            .ToDictionaryAsync(i => i.Id, cancellationToken);
    }

    /// <summary>
    /// Projects to the contract type.
    /// <para>
    /// Always applied <b>after</b> the predicate, never before. Filtering a query
    /// that has already been projected onto a constructor leaves EF trying to
    /// translate <c>new ItemSnapshot(…).Sku == @sku</c>, which it cannot do.
    /// </para>
    /// </summary>
    private static IQueryable<ItemSnapshot> AsSnapshots(IQueryable<Item> items) =>
        items
            .AsNoTracking()
            .Select(i => new ItemSnapshot(
                i.Id,
                i.Sku,
                i.Description,
                i.BaseUom,
                i.IsLotTracked,
                i.IsExpiryTracked,
                i.IsSerialTracked,
                i.IsActive));
}
