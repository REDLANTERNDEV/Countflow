using System.Diagnostics.CodeAnalysis;

namespace Countflow.SharedKernel.Gs1;

/// <summary>
/// Serial Shipping Container Code — the licence plate on a pallet or carton
/// (GS1 AI 00). Eighteen digits with the same modulo-10 check digit as a GTIN.
/// <para>
/// Validated wherever one is created, because an LPN built from a mis-read
/// licence plate follows that pallet around the warehouse until someone notices,
/// and by then everything posted against it is attributed to a container that
/// does not exist.
/// </para>
/// </summary>
public static class Sscc
{
    public const int Length = 18;

    public static bool IsValid([NotNullWhen(true)] string? candidate) =>
        TryParse(candidate, out _);

    /// <summary>
    /// Accepts an SSCC with spaces or GS1 parenthesised formatting stripped, and
    /// yields the bare 18 digits. Identifiers are stored verbatim, so this
    /// normalises punctuation only — never the digits.
    /// </summary>
    public static bool TryParse(string? candidate, [NotNullWhen(true)] out string? sscc)
    {
        sscc = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Replace(" ", string.Empty).Replace("-", string.Empty);

        if (trimmed.Length != Length || !trimmed.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!Gtin.HasValidCheckDigit(trimmed))
        {
            return false;
        }

        sscc = trimmed;
        return true;
    }

    /// <summary>
    /// Builds a valid SSCC from a 17-digit payload by appending its check digit.
    /// Used by the seeder and by tests, which need licence plates that survive
    /// the same validation real ones do.
    /// </summary>
    public static string FromPayload(string seventeenDigits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seventeenDigits);

        if (seventeenDigits.Length != Length - 1 || !seventeenDigits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"An SSCC payload is {Length - 1} digits.", nameof(seventeenDigits));
        }

        return seventeenDigits + Gtin.CalculateCheckDigit(seventeenDigits);
    }
}
