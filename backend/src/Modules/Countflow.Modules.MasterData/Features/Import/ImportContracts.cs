namespace Countflow.Modules.MasterData.Features.Import;

public sealed record ImportIssue(int Row, string Field, string Message);

public sealed record ImportResult(
    bool DryRun,
    bool Committed,
    int RowsRead,
    int WouldCreate,
    int WouldUpdate,
    int Unchanged,
    IReadOnlyList<ImportIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// One row of a location import file.
/// <para>
/// Deliberately flat and close to the CSV: a warehouse manager assembles these
/// in a spreadsheet, and every layer of cleverness between their columns and
/// this record is a place the mapping can go wrong silently.
/// </para>
/// </summary>
public sealed class LocationImportRow
{
    public string Code { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Area code. Required alongside <see cref="Zone"/> because zone codes are
    /// only unique within an area — a DC routinely has a "DOCK" zone in both
    /// receiving and shipping.
    /// </summary>
    public string? Area { get; set; }

    public string? Zone { get; set; }
    public string? Aisle { get; set; }
    public string? Bay { get; set; }
    public string? Level { get; set; }
    public string? Position { get; set; }
    public int? PickSequence { get; set; }
    public decimal? MaxVolumeM3 { get; set; }
    public decimal? MaxWeightKg { get; set; }
    public int? MaxDistinctItems { get; set; }
    public bool? IsPickable { get; set; }
    public bool? IsReplenishable { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class ItemImportRow
{
    public string Sku { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BaseUom { get; set; }
    public string? AbcClass { get; set; }
    public string? TemperatureClass { get; set; }
    public bool? IsLotTracked { get; set; }
    public bool? IsSerialTracked { get; set; }
    public bool? IsExpiryTracked { get; set; }
    public int? ShelfLifeDays { get; set; }
    public bool? IsHazardous { get; set; }

    // Case level only. A full packaging hierarchy needs its own file; this
    // covers the common case of loading a catalogue with one scannable barcode.
    public string? CaseGtin { get; set; }
    public int? CaseQuantity { get; set; }
    public decimal? CaseWeightKg { get; set; }
}
