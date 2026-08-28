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

    /// <summary>
    /// The GS1 company prefix this site issues SSCCs on, without the extension
    /// digit — six to ten digits, allocated to the company by GS1 and typed in
    /// once.
    /// <para>
    /// Null means this warehouse cannot mint licence plates, which is the correct
    /// state for a site that only ever relabels what its suppliers sent. It is
    /// nullable rather than defaulted for the reason set out on <c>ISsccMint</c>:
    /// a plausible-looking default would put another company's prefix on this
    /// company's pallets, and that is a label that scans correctly and is wrong
    /// everywhere outside this building.
    /// </para>
    /// </summary>
    public string? Gs1CompanyPrefix { get; set; }

    /// <summary>
    /// The SSCC's leading extension digit, 0–9. Chosen by the company to widen
    /// the serial range; there is no meaning attached to the value, and 0 is the
    /// ordinary choice.
    /// </summary>
    public int SsccExtensionDigit { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ICollection<Area> Areas { get; init; } = [];

    public ICollection<Location> Locations { get; init; } = [];
}
