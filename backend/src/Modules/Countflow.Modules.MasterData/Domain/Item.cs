namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// A stock-keeping unit. Holds identity and the policies that govern how it is
/// handled; physical dimensions live on <see cref="PackConfiguration"/> because
/// they differ per packaging level.
/// </summary>
public sealed class Item
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Internal item number. The thing people say on the phone.</summary>
    public required string Sku { get; set; }

    public required string Description { get; set; }

    /// <summary>Unit inventory is counted in — "EA", "KG", "L".</summary>
    public required string BaseUom { get; set; }

    public AbcClass AbcClass { get; set; } = AbcClass.C;

    public TemperatureClass TemperatureClass { get; set; } = TemperatureClass.Ambient;

    // ---- Tracking policy ----
    //
    // These decide how much a scan has to capture. Turning lot tracking on for
    // an item that already holds stock is a data-migration problem, so they are
    // set deliberately at item creation.

    /// <summary>Requires a batch/lot number at receipt, and enables FEFO allocation.</summary>
    public bool IsLotTracked { get; set; }

    /// <summary>Requires a unique serial per unit. Expensive to scan; reserve it for high-value goods.</summary>
    public bool IsSerialTracked { get; set; }

    /// <summary>Requires an expiry date at receipt. Implies FEFO rather than FIFO rotation.</summary>
    public bool IsExpiryTracked { get; set; }

    /// <summary>
    /// Used to derive an expiry date when a supplier gives a production date
    /// instead, and to flag stock approaching end of life.
    /// </summary>
    public int? ShelfLifeDays { get; set; }

    /// <summary>
    /// Minimum shelf life a customer will accept on receipt, as a percentage of
    /// total. Stock inside this window is still sellable but must not be
    /// allocated to an outbound order — a real and frequently-missed rule in
    /// food and pharma.
    /// </summary>
    public int? MinimumRemainingShelfLifePercent { get; set; }

    public bool IsHazardous { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ICollection<PackConfiguration> PackConfigurations { get; init; } = [];
}
