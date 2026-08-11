namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// What another module is allowed to know about an item.
/// <para>
/// The tracking flags are here because they are policy, not description: the
/// ledger has to refuse a movement of a lot-tracked item that arrives without a
/// lot, and it cannot ask the item catalogue's tables directly.
/// </para>
/// </summary>
public sealed record ItemSnapshot(
    Guid Id,
    string Sku,
    string Description,
    string BaseUom,
    bool IsLotTracked,
    bool IsExpiryTracked,
    bool IsSerialTracked,
    bool IsActive);

/// <summary>MasterData's published read surface for items. See <see cref="ILocationDirectory"/>.</summary>
public interface IItemDirectory
{
    Task<ItemSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ItemSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ItemSnapshot>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);
}
