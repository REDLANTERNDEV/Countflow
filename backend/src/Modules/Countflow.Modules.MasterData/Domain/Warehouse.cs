namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// A physical facility — the root of the location topology
/// (Warehouse → Area → Zone → Aisle → Bay → Level → Position).
/// Phase 1 builds the rest of the tree beneath this.
/// </summary>
public sealed class Warehouse
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Short code used as the first segment of every location code, e.g. "WH1".</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? City { get; set; }

    public string? CountryCode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ICollection<Area> Areas { get; init; } = [];

    public ICollection<Location> Locations { get; init; } = [];
}
