using System.Text.RegularExpressions;

namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// Builds and validates location codes.
/// <para>
/// The scheme is <c>WAREHOUSE-AREA-ZONE-AISLE-BAY-LEVEL-POSITION</c>, e.g.
/// <c>WH1-STOR-AMB-A07-03-02-B</c>. Keeping it strict and generated in one place
/// matters more than it looks: these codes go onto physical rack labels, and a
/// warehouse that has been running for a year cannot cheaply relabel 20,000
/// bins because two segments were assembled inconsistently.
/// </para>
/// </summary>
public static partial class LocationCode
{
    public const char Separator = '-';

    /// <summary>Segments are upper-case alphanumeric. No spaces: these are scanned and spoken aloud.</summary>
    [GeneratedRegex("^[A-Z0-9]{1,8}$")]
    private static partial Regex SegmentPattern { get; }

    public static string ForBin(
        string warehouseCode,
        string areaCode,
        string zoneCode,
        string aisle,
        string bay,
        string level,
        string position)
    {
        string[] segments = [warehouseCode, areaCode, zoneCode, aisle, bay, level, position];

        foreach (var segment in segments)
        {
            if (!SegmentPattern.IsMatch(segment))
            {
                throw new ArgumentException(
                    $"'{segment}' is not a valid location code segment (1-8 upper-case alphanumeric characters).");
            }
        }

        return string.Join(Separator, segments);
    }

    /// <summary>
    /// Virtual locations get a short, obviously-not-a-bin code such as
    /// <c>WH1-VIRT-SUPPLIER</c>, so a ledger entry reads clearly and nobody
    /// mistakes a counterparty for somewhere they can walk to.
    /// </summary>
    public static string ForVirtual(string warehouseCode, LocationType type)
    {
        if (!type.IsVirtual())
        {
            throw new ArgumentException($"{type} is a physical location type.", nameof(type));
        }

        return $"{warehouseCode}{Separator}VIRT{Separator}{type.ToString().ToUpperInvariant()}";
    }

    public static bool IsValidSegment(string segment) => SegmentPattern.IsMatch(segment);
}
