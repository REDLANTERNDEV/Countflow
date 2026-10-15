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

    /// <summary>
    /// Which named heuristic orders the lines of a pick list inside this zone.
    /// <para>
    /// Reference data, in the same drawer as <see cref="TemperatureClass"/> and
    /// <see cref="PickSequence"/>, because a routing strategy is a fact about the
    /// physical shape of a zone rather than a tunable weight. It is deliberately not
    /// a rule set: a rule set combines its rules by weighting them, and S-Shape and
    /// Midpoint are mutually exclusive algorithms that cannot be blended into
    /// anything that is still a route. See
    /// <c>Countflow.SharedKernel.Routing.PickRouteCatalogue</c> for the codes and
    /// <c>decision 15</c> for the argument.
    /// </para>
    /// <para>
    /// Validated against the catalogue <i>and</i> against this zone's own geometry
    /// when it is set, so a hall whose bins record no aisle is refused S-Shape at the
    /// moment somebody chooses it rather than silently walked some other way at six
    /// in the morning.
    /// </para>
    /// </summary>
    public string PickRouteStrategy { get; set; } = "route.sequence_ordinal";

    /// <summary>
    /// Whether a picker can leave an aisle at the far end instead of walking back out.
    /// <para>
    /// The one fact routing needs that nothing in the model could imply: locations
    /// record where bins are and never how somebody gets between them. It is what
    /// separates the strategies that traverse an aisle from the strategies that go in
    /// and come back, so it gates S-Shape and Midpoint outright.
    /// </para>
    /// <para>
    /// False by default, and deliberately the pessimistic direction. A zone wrongly
    /// marked as having one sends every picker in it down a dead end; a zone wrongly
    /// marked as not having one costs some walking and nothing else.
    /// </para>
    /// </summary>
    public bool HasRearCrossAisle { get; set; }

    public bool IsActive { get; set; } = true;

    public Area? Area { get; init; }

    public ICollection<Location> Locations { get; init; } = [];
}
