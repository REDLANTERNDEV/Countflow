namespace Countflow.SharedKernel.Gs1;

public enum Gs1DataKind
{
    Numeric,
    Alphanumeric,
    Date,
    Decimal
}

public sealed record Gs1AiDefinition(string Ai, string Title, Gs1DataKind Kind, int MaxDataLength);

/// <summary>
/// The subset of GS1 Application Identifiers a warehouse actually meets, plus
/// the length rules needed to split a concatenated element string correctly.
/// </summary>
public static class Gs1ApplicationIdentifiers
{
    /// <summary>
    /// Predefined fixed-length element strings, keyed by the AI's first two
    /// digits, valued by the <em>total</em> length including the AI.
    /// <para>
    /// This table is the crux of parsing. These AIs carry no separator, so a
    /// parser that does not know them will swallow the following AI as data.
    /// Everything not listed here is variable length and must be terminated by
    /// FNC1/GS or be the last element in the string.
    /// </para>
    /// <para>Source: GS1 General Specifications, "Predefined length" table.</para>
    /// </summary>
    private static readonly Dictionary<string, int> PredefinedTotalLengths = new()
    {
        ["00"] = 20, // SSCC:                AI 2 + 18
        ["01"] = 16, // GTIN:                AI 2 + 14
        ["02"] = 16, // GTIN of contents:    AI 2 + 14
        ["03"] = 16,
        ["04"] = 18,
        ["11"] = 8,  // Production date:     AI 2 + YYMMDD
        ["12"] = 8,  // Due date
        ["13"] = 8,  // Packaging date
        ["14"] = 8,
        ["15"] = 8,  // Best before
        ["16"] = 8,  // Sell by
        ["17"] = 8,  // Expiry
        ["18"] = 8,
        ["19"] = 8,
        ["20"] = 4,  // Variant:             AI 2 + 2
        ["31"] = 10, // Measures:            AI 4 + 6
        ["32"] = 10,
        ["33"] = 10,
        ["34"] = 10,
        ["35"] = 10,
        ["36"] = 10,
        ["41"] = 16  // GLN:                 AI 3 + 13
    };

    /// <summary>
    /// Two-digit prefixes that introduce a four-digit AI. The trailing digit of
    /// a measure AI (3100–3105) is the number of implied decimal places, which
    /// is why these cannot be treated as two-digit AIs with numeric data.
    /// </summary>
    private static readonly HashSet<string> FourDigitPrefixes =
    [
        "31", "32", "33", "34", "35", "36", "39",
        "70", "71", "72",
        "80", "81", "82", "83", "84", "85", "86", "87", "88", "89"
    ];

    private static readonly HashSet<string> ThreeDigitPrefixes = ["40", "41", "42", "43"];

    private static readonly Dictionary<string, Gs1AiDefinition> Known = new Gs1AiDefinition[]
    {
        new("00", "SSCC", Gs1DataKind.Numeric, 18),
        new("01", "GTIN", Gs1DataKind.Numeric, 14),
        new("02", "GTIN of contained trade items", Gs1DataKind.Numeric, 14),
        new("10", "Batch or lot number", Gs1DataKind.Alphanumeric, 20),
        new("11", "Production date", Gs1DataKind.Date, 6),
        new("12", "Due date", Gs1DataKind.Date, 6),
        new("13", "Packaging date", Gs1DataKind.Date, 6),
        new("15", "Best before date", Gs1DataKind.Date, 6),
        new("16", "Sell by date", Gs1DataKind.Date, 6),
        new("17", "Expiration date", Gs1DataKind.Date, 6),
        new("20", "Product variant", Gs1DataKind.Numeric, 2),
        new("21", "Serial number", Gs1DataKind.Alphanumeric, 20),
        new("22", "Consumer product variant", Gs1DataKind.Alphanumeric, 20),
        new("30", "Variable count", Gs1DataKind.Numeric, 8),
        new("37", "Count of trade items", Gs1DataKind.Numeric, 8),
        new("240", "Additional product identification", Gs1DataKind.Alphanumeric, 30),
        new("241", "Customer part number", Gs1DataKind.Alphanumeric, 30),
        new("400", "Customer purchase order number", Gs1DataKind.Alphanumeric, 30),
        new("401", "Consignment number (GINC)", Gs1DataKind.Alphanumeric, 30),
        new("402", "Shipment identification (GSIN)", Gs1DataKind.Numeric, 17),
        new("403", "Routing code", Gs1DataKind.Alphanumeric, 30),
        new("410", "Ship to GLN", Gs1DataKind.Numeric, 13),
        new("411", "Bill to GLN", Gs1DataKind.Numeric, 13),
        new("412", "Purchased from GLN", Gs1DataKind.Numeric, 13),
        new("413", "Ship for GLN", Gs1DataKind.Numeric, 13),
        new("414", "Physical location GLN", Gs1DataKind.Numeric, 13),
        new("415", "Invoicing party GLN", Gs1DataKind.Numeric, 13),
        new("420", "Ship to postal code", Gs1DataKind.Alphanumeric, 20),
        new("421", "Ship to postal code with country", Gs1DataKind.Alphanumeric, 12),
        new("422", "Country of origin", Gs1DataKind.Numeric, 3),
        new("7003", "Expiration date and time", Gs1DataKind.Alphanumeric, 10),
        new("8020", "Payment slip reference", Gs1DataKind.Alphanumeric, 25)
    }.ToDictionary(definition => definition.Ai);

    static Gs1ApplicationIdentifiers()
    {
        // Measure AIs come in six variants each, the final digit giving the
        // implied decimal position — 3100 is whole kilograms, 3103 is grams.
        (string Prefix, string Title)[] measures =
        [
            ("310", "Net weight (kg)"),
            ("311", "Length (m)"),
            ("312", "Width (m)"),
            ("313", "Height (m)"),
            ("314", "Area (m²)"),
            ("315", "Net volume (l)"),
            ("316", "Net volume (m³)"),
            ("320", "Net weight (lb)"),
            ("330", "Gross weight (kg)"),
            ("331", "Length, logistic (m)"),
            ("332", "Width, logistic (m)"),
            ("333", "Height, logistic (m)"),
            ("340", "Gross weight (lb)"),
            ("350", "Area, logistic (m²)"),
            ("360", "Net volume, logistic (l)")
        ];

        foreach (var (prefix, title) in measures)
        {
            for (var decimals = 0; decimals <= 5; decimals++)
            {
                var ai = $"{prefix}{decimals}";
                Known[ai] = new Gs1AiDefinition(ai, title, Gs1DataKind.Decimal, 6);
            }
        }
    }

    public static bool TryGetDefinition(string ai, out Gs1AiDefinition? definition) =>
        Known.TryGetValue(ai, out definition);

    /// <summary>Total element length including the AI, when the AI has a predefined length.</summary>
    public static bool TryGetPredefinedTotalLength(string twoDigitPrefix, out int totalLength) =>
        PredefinedTotalLengths.TryGetValue(twoDigitPrefix, out totalLength);

    /// <summary>
    /// How many characters the AI itself occupies. GS1 AIs are two to four
    /// digits and the length is implied by the leading digits rather than
    /// delimited, so this lookup has to happen before the data can be read.
    /// </summary>
    public static int GetAiLength(string twoDigitPrefix)
    {
        if (FourDigitPrefixes.Contains(twoDigitPrefix))
        {
            return 4;
        }

        return ThreeDigitPrefixes.Contains(twoDigitPrefix) ? 3 : 2;
    }
}
