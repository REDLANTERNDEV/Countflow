using System.Diagnostics.CodeAnalysis;

namespace Countflow.SharedKernel.Gs1;

/// <summary>
/// A Global Trade Item Number, normalised to its 14-digit form.
/// <para>
/// GTIN-8, -12 (UPC-A), and -13 (EAN-13) are all the same number with different
/// leading-zero padding. Normalising on the way in means a case scanned as a
/// GTIN-14 and the same product looked up by its EAN-13 resolve to one record,
/// which is the single most common cause of "the scanner says unknown item".
/// </para>
/// </summary>
public readonly record struct Gtin
{
    private Gtin(string value) => Value = value;

    /// <summary>Always 14 digits.</summary>
    public string Value { get; }

    public static bool TryParse(string? candidate, [NotNullWhen(true)] out Gtin? gtin)
    {
        gtin = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();

        if (trimmed.Length is not (8 or 12 or 13 or 14))
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        var normalised = trimmed.PadLeft(14, '0');

        if (!HasValidCheckDigit(normalised))
        {
            return false;
        }

        gtin = new Gtin(normalised);
        return true;
    }

    public static Gtin Parse(string candidate) =>
        TryParse(candidate, out var gtin)
            ? gtin.Value
            : throw new FormatException($"'{candidate}' is not a valid GTIN.");

    /// <summary>
    /// Modulo-10 check digit, per the GS1 general specification: weight the
    /// digits 3 and 1 alternately from the right of the payload, and the check
    /// digit is whatever rounds the total up to a multiple of ten.
    /// </summary>
    public static bool HasValidCheckDigit(string digits)
    {
        if (digits.Length < 2)
        {
            return false;
        }

        var payload = digits[..^1];
        var expected = digits[^1] - '0';

        var sum = 0;
        var weight = 3;

        for (var index = payload.Length - 1; index >= 0; index--)
        {
            sum += (payload[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - (sum % 10)) % 10 == expected;
    }

    /// <summary>Computes the check digit for a payload that does not yet carry one.</summary>
    public static char CalculateCheckDigit(string payload)
    {
        var sum = 0;
        var weight = 3;

        for (var index = payload.Length - 1; index >= 0; index--)
        {
            sum += (payload[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }

    public override string ToString() => Value;
}
