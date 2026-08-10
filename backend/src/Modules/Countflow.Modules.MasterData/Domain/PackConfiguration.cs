namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// One rung of an item's packaging hierarchy: its GTIN, how many base units it
/// contains, and its physical envelope.
/// <para>
/// Volume and weight here are what putaway checks a candidate location's
/// capacity against, so an item with no case dimensions cannot be slotted
/// intelligently — which is why bad master data sinks WMS projects.
/// </para>
/// </summary>
public sealed class PackConfiguration
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ItemId { get; init; }

    public required PackagingLevel Level { get; set; }

    /// <summary>
    /// How many base units this level contains. Each = 1, a case of 24 = 24.
    /// Everything is converted to base units before it reaches the ledger, so
    /// stock arithmetic never has to reason about mixed units.
    /// </summary>
    public required int BaseUnitQuantity { get; set; }

    /// <summary>
    /// GTIN for this specific level, normalised to 14 digits. Nullable: plenty
    /// of real items have a barcode on the case but not on the inner.
    /// </summary>
    public string? Gtin { get; set; }

    public decimal? LengthMm { get; set; }

    public decimal? WidthMm { get; set; }

    public decimal? HeightMm { get; set; }

    /// <summary>Gross weight — contents plus packaging, which is what the rack actually carries.</summary>
    public decimal? GrossWeightKg { get; set; }

    /// <summary>The level goods are normally received and put away in. Usually Case.</summary>
    public bool IsReceivingUnit { get; set; }

    /// <summary>The level pickers are normally directed to pick in.</summary>
    public bool IsPickUnit { get; set; }

    public Item? Item { get; init; }

    /// <summary>Cubic volume in m³, or null when any dimension is missing.</summary>
    public decimal? VolumeM3 =>
        LengthMm is { } length && WidthMm is { } width && HeightMm is { } height
            ? length * width * height / 1_000_000_000m
            : null;
}
