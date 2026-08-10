namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// An addressable place stock can be. The unit of inventory truth: every stock
/// balance and every ledger movement in Phase 2 references two of these.
/// <para>
/// Aisle, bay, level and position are stored as columns rather than modelled as
/// entities. They are coordinates, not things — nothing hangs off "aisle A07"
/// that is not already on the locations within it, and keeping them flat makes
/// route sorting a plain ORDER BY.
/// </para>
/// </summary>
public sealed class Location
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    /// <summary>Null for virtual locations, which sit outside the physical topology.</summary>
    public Guid? ZoneId { get; init; }

    /// <summary>
    /// Full unique code, e.g. <c>WH1-STOR-AMB-A07-03-02-B</c>. This is what gets
    /// printed on the rack label and scanned, so it is the natural key operators
    /// and support staff actually speak in.
    /// </summary>
    public required string Code { get; set; }

    public required LocationType Type { get; set; }

    // ---- Physical coordinates (null for virtual locations) ----

    public string? Aisle { get; set; }

    public string? Bay { get; set; }

    public string? Level { get; set; }

    public string? Position { get; set; }

    /// <summary>
    /// Walking order within the zone. Pick lists sort by this so a picker
    /// travels one direction down an aisle instead of doubling back — travel is
    /// over half of pick labour, so this number is the cheapest optimisation in
    /// the system.
    /// </summary>
    public int PickSequence { get; set; }

    // ---- Capacity: the hard constraints putaway must respect ----

    public decimal? MaxVolumeM3 { get; set; }

    public decimal? MaxWeightKg { get; set; }

    /// <summary>
    /// Cap on distinct items in this location. Pick faces are usually 1 — mixing
    /// SKUs in one pick slot is how mis-picks happen.
    /// </summary>
    public int? MaxDistinctItems { get; set; }

    /// <summary>False for reserve racking that must be replenished down rather than picked from.</summary>
    public bool IsPickable { get; set; }

    /// <summary>True for pick faces that top up automatically when they run low.</summary>
    public bool IsReplenishable { get; set; }

    /// <summary>
    /// Excludes the location from putaway and allocation without deleting it.
    /// Blocked racking still holds stock and still has history, so it must never
    /// simply disappear.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public Warehouse? Warehouse { get; init; }

    public Zone? Zone { get; init; }

    public bool IsVirtual => Type.IsVirtual();
}
