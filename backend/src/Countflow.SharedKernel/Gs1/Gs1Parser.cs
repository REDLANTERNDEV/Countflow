using System.Globalization;
using System.Text;
using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Gs1;

/// <summary>One decoded element of a GS1 element string.</summary>
public sealed record Gs1Element(string Ai, string Title, string Value)
{
    /// <summary>Populated for date AIs (11, 15, 17, …).</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Populated for measure AIs, with the implied decimal point applied.</summary>
    public decimal? Number { get; init; }
}

public sealed class Gs1ParseResult
{
    public required IReadOnlyList<Gs1Element> Elements { get; init; }

    public string? Find(string ai) => Elements.FirstOrDefault(e => e.Ai == ai)?.Value;

    /// <summary>GTIN (AI 01), normalised to 14 digits.</summary>
    public Gtin? Gtin =>
        Gs1.Gtin.TryParse(Find("01"), out var gtin) ? gtin : null;

    /// <summary>SSCC (AI 00) — the licence plate on a pallet or carton.</summary>
    public string? Sscc => Find("00");

    /// <summary>
    /// Whether the SSCC's check digit holds. Same modulo-10 scheme as a GTIN.
    /// Checked at scan time because an LPN created from a mis-read licence plate
    /// follows that pallet around the warehouse until someone notices.
    /// </summary>
    public bool HasValidSscc => Sscc is { Length: 18 } sscc && Gs1.Gtin.HasValidCheckDigit(sscc);

    /// <summary>Batch or lot number (AI 10).</summary>
    public string? LotNumber => Find("10");

    /// <summary>Serial number (AI 21).</summary>
    public string? SerialNumber => Find("21");

    public DateOnly? ExpiryDate => Elements.FirstOrDefault(e => e.Ai == "17")?.Date;

    public DateOnly? ProductionDate => Elements.FirstOrDefault(e => e.Ai == "11")?.Date;

    public DateOnly? BestBeforeDate => Elements.FirstOrDefault(e => e.Ai == "15")?.Date;

    /// <summary>Variable count (AI 30) or count of contained trade items (AI 37).</summary>
    public int? Quantity =>
        int.TryParse(Find("30") ?? Find("37"), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
            ? quantity
            : null;

    /// <summary>Net weight in kilograms, from whichever 310n variant was present.</summary>
    public decimal? NetWeightKg =>
        Elements.FirstOrDefault(e => e.Ai.StartsWith("310", StringComparison.Ordinal))?.Number;
}

/// <summary>
/// Parses GS1 element strings as produced by GS1-128, GS1 DataMatrix and GS1
/// DataBar scanners.
/// <para>
/// One scan of a logistics label yields product, batch, expiry and quantity
/// together, which is the whole reason a WMS can verify a receipt instead of
/// trusting a typed number. Getting the length rules right matters: an AI whose
/// predefined length the parser does not know will silently absorb the next
/// AI's digits and produce a plausible-looking wrong answer.
/// </para>
/// </summary>
public static class Gs1Parser
{
    /// <summary>
    /// FNC1 as transmitted by a scanner in keyboard-wedge mode. Most devices
    /// send ASCII 29 (GS); some are configured to send a printable placeholder,
    /// which is why the alternatives below are also accepted.
    /// </summary>
    public const char GroupSeparator = '';

    private static readonly string[] SymbologyIdentifiers = ["]C1", "]e0", "]d2", "]Q3", "]J1"];

    private static readonly Error EmptyInput =
        Error.Validation("gs1.empty", "Nothing was scanned.");

    public static Result<Gs1ParseResult> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Failure<Gs1ParseResult>(EmptyInput);
        }

        var data = Normalise(raw);

        if (data.Length == 0)
        {
            return Result.Failure<Gs1ParseResult>(EmptyInput);
        }

        var elements = new List<Gs1Element>();
        var position = 0;

        while (position < data.Length)
        {
            // Tolerate stray separators, including a trailing one.
            if (data[position] == GroupSeparator)
            {
                position++;
                continue;
            }

            if (data.Length - position < 2)
            {
                return Result.Failure<Gs1ParseResult>(Error.Validation(
                    "gs1.truncated_ai",
                    $"Trailing characters '{data[position..]}' are too short to be an application identifier."));
            }

            var prefix = data.Substring(position, 2);

            if (!char.IsAsciiDigit(prefix[0]) || !char.IsAsciiDigit(prefix[1]))
            {
                return Result.Failure<Gs1ParseResult>(Error.Validation(
                    "gs1.invalid_ai",
                    $"Expected an application identifier at position {position} but found '{prefix}'."));
            }

            var aiLength = Gs1ApplicationIdentifiers.GetAiLength(prefix);

            if (position + aiLength > data.Length)
            {
                return Result.Failure<Gs1ParseResult>(Error.Validation(
                    "gs1.truncated_ai",
                    $"Application identifier starting '{prefix}' is truncated."));
            }

            var ai = data.Substring(position, aiLength);
            position += aiLength;

            string value;

            if (Gs1ApplicationIdentifiers.TryGetPredefinedTotalLength(prefix, out var totalLength))
            {
                // Predefined length: read exactly, and never look for a separator.
                var dataLength = totalLength - aiLength;

                if (position + dataLength > data.Length)
                {
                    return Result.Failure<Gs1ParseResult>(Error.Validation(
                        "gs1.truncated_value",
                        $"AI {ai} needs {dataLength} characters but only {data.Length - position} remain."));
                }

                value = data.Substring(position, dataLength);
                position += dataLength;
            }
            else
            {
                // Variable length: run to the next separator, or to the end.
                var separator = data.IndexOf(GroupSeparator, position);
                var end = separator < 0 ? data.Length : separator;

                value = data[position..end];
                position = separator < 0 ? data.Length : separator + 1;

                if (value.Length == 0)
                {
                    return Result.Failure<Gs1ParseResult>(Error.Validation(
                        "gs1.empty_value",
                        $"AI {ai} carries no data."));
                }
            }

            var result = Interpret(ai, value);
            if (result.IsFailure)
            {
                return Result.Failure<Gs1ParseResult>(result.Error);
            }

            elements.Add(result.Value);
        }

        if (elements.Count == 0)
        {
            return Result.Failure<Gs1ParseResult>(EmptyInput);
        }

        return Result.Success(new Gs1ParseResult { Elements = elements });
    }

    /// <summary>
    /// Strips the symbology identifier a scanner may prepend, and accepts the
    /// human-readable bracketed form — <c>(01)0950…(10)ABC</c> — which is how
    /// these strings appear in supplier specs, test fixtures and support
    /// tickets. Being able to paste one straight in is worth the few lines.
    /// </summary>
    private static string Normalise(string raw)
    {
        var value = raw.Trim();

        foreach (var identifier in SymbologyIdentifiers)
        {
            if (value.StartsWith(identifier, StringComparison.Ordinal))
            {
                value = value[identifier.Length..];
                break;
            }
        }

        // Some configurations emit a printable stand-in for FNC1.
        value = value
            .Replace("<GS>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("{GS}", "", StringComparison.OrdinalIgnoreCase);

        if (!value.Contains('(', StringComparison.Ordinal))
        {
            return value;
        }

        // Bracketed form: "(01)…(10)…" becomes "01…" + GS + "10…". A separator
        // is inserted after every variable-length element; the fixed-length
        // reader ignores separators it does not need.
        var builder = new StringBuilder();
        var index = 0;

        while (index < value.Length)
        {
            var open = value.IndexOf('(', index);
            if (open < 0)
            {
                builder.Append(value[index..]);
                break;
            }

            var close = value.IndexOf(')', open);
            if (close < 0)
            {
                builder.Append(value[index..]);
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(GroupSeparator);
            }

            var ai = value[(open + 1)..close];
            var nextOpen = value.IndexOf('(', close);
            var end = nextOpen < 0 ? value.Length : nextOpen;

            builder.Append(ai).Append(value[(close + 1)..end]);
            index = end;
        }

        return builder.ToString();
    }

    private static Result<Gs1Element> Interpret(string ai, string value)
    {
        var title = Gs1ApplicationIdentifiers.TryGetDefinition(ai, out var definition)
            ? definition!.Title
            : $"AI {ai}";

        if (definition is null)
        {
            // Unknown AIs are surfaced rather than rejected: a supplier adding
            // an AI we have not catalogued should not make an otherwise valid
            // label unscannable.
            return Result.Success(new Gs1Element(ai, title, value));
        }

        if (value.Length > definition.MaxDataLength)
        {
            return Result.Failure<Gs1Element>(Error.Validation(
                "gs1.value_too_long",
                $"AI {ai} ({title}) allows at most {definition.MaxDataLength} characters but got {value.Length}."));
        }

        switch (definition.Kind)
        {
            case Gs1DataKind.Numeric:
                if (!value.All(char.IsAsciiDigit))
                {
                    return Result.Failure<Gs1Element>(Error.Validation(
                        "gs1.non_numeric",
                        $"AI {ai} ({title}) must be numeric but got '{value}'."));
                }

                return Result.Success(new Gs1Element(ai, title, value));

            case Gs1DataKind.Date:
                var date = ParseDate(value);
                if (date is null)
                {
                    return Result.Failure<Gs1Element>(Error.Validation(
                        "gs1.invalid_date",
                        $"AI {ai} ({title}) is not a valid YYMMDD date: '{value}'."));
                }

                return Result.Success(new Gs1Element(ai, title, value) { Date = date });

            case Gs1DataKind.Decimal:
                if (!value.All(char.IsAsciiDigit))
                {
                    return Result.Failure<Gs1Element>(Error.Validation(
                        "gs1.non_numeric",
                        $"AI {ai} ({title}) must be numeric but got '{value}'."));
                }

                // The AI's last digit says where the decimal point goes.
                var decimals = ai[^1] - '0';
                var scaled = decimal.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
                    / (decimal)Math.Pow(10, decimals);

                return Result.Success(new Gs1Element(ai, title, value) { Number = scaled });

            default:
                return Result.Success(new Gs1Element(ai, title, value));
        }
    }

    /// <summary>
    /// GS1 dates are YYMMDD. A day of "00" means "the end of that month", which
    /// suppliers use for best-before dates; treating it as an invalid date
    /// rejects perfectly good labels.
    /// </summary>
    private static DateOnly? ParseDate(string value)
    {
        if (value.Length != 6 || !value.All(char.IsAsciiDigit))
        {
            return null;
        }

        var yy = int.Parse(value[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(value.Substring(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(value.Substring(4, 2), CultureInfo.InvariantCulture);

        if (month is < 1 or > 12)
        {
            return null;
        }

        var year = ResolveYear(yy);

        if (day == 0)
        {
            return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        }

        return day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    /// <summary>
    /// Two-digit years resolve into a sliding window: anything more than 50
    /// years ahead is read as the past instead. Per the GS1 general
    /// specification — a "26" scanned in 2026 is this year, not 1926.
    /// </summary>
    private static int ResolveYear(int twoDigitYear)
    {
        var currentYear = DateTime.UtcNow.Year;
        var candidate = (currentYear - (currentYear % 100)) + twoDigitYear;

        return (candidate - currentYear) switch
        {
            >= 51 => candidate - 100,
            <= -50 => candidate + 100,
            _ => candidate
        };
    }
}
