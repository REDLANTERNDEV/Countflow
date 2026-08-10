namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// A handling regime within an area — typically a temperature band, but also
/// used to fence off hazardous or high-value stock. The third segment of a
/// location code.
/// </summary>
public sealed class Zone
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid AreaId { get; init; }

    /// <summary>Short code, e.g. "AMB". Unique within its area.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public TemperatureClass TemperatureClass { get; set; } = TemperatureClass.Ambient;

    /// <summary>
    /// Orders zones along a picker's natural walking route. Wave routing sorts
    /// by this before location pick sequence, so a pick list never sends
    /// somebody back and forth between zones.
    /// </summary>
    public int PickSequence { get; set; }

    public bool IsActive { get; set; } = true;

    public Area? Area { get; init; }

    public ICollection<Location> Locations { get; init; } = [];
}
