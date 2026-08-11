namespace Countflow.SharedKernel.Contracts;

/// <summary>What another module is allowed to know about a site.</summary>
public sealed record WarehouseSnapshot(Guid Id, string Code, string Name, bool IsActive);

/// <summary>
/// MasterData's published read surface for warehouses.
/// <para>
/// Small, and separate from <see cref="ILocationDirectory"/> on purpose. A
/// warehouse is not a location — nothing is ever stored "in WH1" — and folding
/// the lookup into the location directory would blur the one distinction that
/// stops a module treating a site as a bin. Every document that arrives from
/// outside the building names its destination by this code, so the inbound loop
/// needs it and the outbound loop will too.
/// </para>
/// </summary>
public interface IWarehouseDirectory
{
    Task<WarehouseSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<WarehouseSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<WarehouseSnapshot>> ListAsync(CancellationToken cancellationToken);
}
