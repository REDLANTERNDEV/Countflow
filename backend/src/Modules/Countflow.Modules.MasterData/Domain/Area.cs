namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// Functional division of a warehouse — receiving, storage, packing, shipping.
/// The second segment of a location code.
/// </summary>
public sealed class Area
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>Short code, e.g. "STOR". Unique within its warehouse.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public Warehouse? Warehouse { get; init; }

    public ICollection<Zone> Zones { get; init; } = [];
}
