using System.Text;
using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Gs1;

/// <summary>
/// A GS1 element string being built, rather than one being read.
/// <para>
/// <see cref="Gs1Parser"/> is the other direction and the older one: it takes what
/// a scanner sends and works out what it means. This takes what the warehouse
/// means and works out what a symbol has to say — which is a different job with a
/// different failure mode. A parser that gets it wrong rejects a scan and somebody
/// scans again. A builder that gets it wrong prints two thousand pallet labels
/// carrying a barcode that no scanner in the supply chain can split back into
/// fields, and nobody finds out until the goods reach a customer's dock.
/// </para>
/// <para>
/// <b>The separator is the whole difficulty.</b> An element whose AI has a
/// predefined length is self-terminating and must <i>not</i> be followed by FNC1;
/// a variable-length one must be, unless it happens to be last. Get that backwards
/// in either direction and the string still encodes, still prints, and still
/// decodes — into the wrong fields. So this type owns the placement, and callers
/// never concatenate AIs by hand.
/// </para>
/// <para>
/// The FNC1 <i>representation</i> is deliberately not fixed here, because it
/// differs by consumer: ZPL wants <c>&gt;8</c>, a raw scan carries ASCII 29, and a
/// human-readable line wants no separator at all but brackets round every AI. One
/// string, three renderings — see <see cref="Encode"/> and
/// <see cref="HumanReadable"/>.
/// </para>
/// </summary>
public sealed class Gs1ElementString
{
    /// <summary>ASCII 29, the group separator a scanner sends for FNC1 mid-string.</summary>
    public const string AsciiGroupSeparator = "\u001d";

    /// <summary>How ZPL's <c>^BC</c> and <c>^BX</c> spell FNC1 inside <c>^FD</c> data.</summary>
    public const string ZplFunction1 = ">8";

    private Gs1ElementString(IReadOnlyList<Gs1Element> elements) => Elements = elements;

    public IReadOnlyList<Gs1Element> Elements { get; }

    /// <summary>
    /// Builds an element string, refusing anything that would not decode back to
    /// what was put in.
    /// <para>
    /// Order is the caller's, and it is preserved rather than sorted. Putting the
    /// predefined-length AIs first is the convention because it costs fewer
    /// separators, but it is a convention about efficiency and not about meaning,
    /// and a builder that quietly reordered would make a printed label disagree
    /// with the template that a supervisor laid out.
    /// </para>
    /// </summary>
    public static Result<Gs1ElementString> Build(IReadOnlyList<(string Ai, string Value)> elements)
    {
        if (elements.Count == 0)
        {
            return Result.Failure<Gs1ElementString>(Gs1Errors.NoElements);
        }

        var built = new List<Gs1Element>(elements.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (ai, value) in elements)
        {
            if (!seen.Add(ai))
            {
                return Result.Failure<Gs1ElementString>(Gs1Errors.DuplicateAi(ai));
            }

            if (!Gs1ApplicationIdentifiers.TryGetDefinition(ai, out var definition) || definition is null)
            {
                return Result.Failure<Gs1ElementString>(Gs1Errors.UnknownAi(ai));
            }

            if (string.IsNullOrEmpty(value))
            {
                // Never silently dropped. An AI the caller asked for and this
                // refused to emit is a field missing from a label that claims to
                // carry it, and the caller is the only code that can decide
                // whether the right answer is "omit it" or "do not print".
                return Result.Failure<Gs1ElementString>(Gs1Errors.EmptyValue(ai));
            }

            if (value.Length > definition.MaxDataLength)
            {
                return Result.Failure<Gs1ElementString>(Gs1Errors.ValueTooLong(ai, definition.MaxDataLength));
            }

            if (definition.Kind is Gs1DataKind.Numeric or Gs1DataKind.Date or Gs1DataKind.Decimal
                && !value.All(char.IsAsciiDigit))
            {
                return Result.Failure<Gs1ElementString>(Gs1Errors.NonNumeric(ai));
            }

            // A predefined-length AI carries no separator, so a value even one
            // character short would swallow the head of whatever follows it. This
            // is the one length check that cannot be a warning.
            if (HasPredefinedLength(ai, out var totalLength)
                && ai.Length + value.Length != totalLength)
            {
                return Result.Failure<Gs1ElementString>(
                    Gs1Errors.WrongFixedLength(ai, totalLength - ai.Length));
            }

            built.Add(new Gs1Element(ai, definition.Title, value));
        }

        return new Gs1ElementString(built);
    }

    /// <summary>
    /// The machine-readable string, with <paramref name="fnc1"/> standing in for
    /// the separator wherever one is required — and nowhere else.
    /// </summary>
    public string Encode(string fnc1)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < Elements.Count; index++)
        {
            var element = Elements[index];

            builder.Append(element.Ai).Append(element.Value);

            // Only a variable-length element needs terminating, and only when
            // something follows it. The last element is terminated by the end of
            // the symbol.
            if (index < Elements.Count - 1 && !HasPredefinedLength(element.Ai, out _))
            {
                builder.Append(fnc1);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The line printed under the barcode, AIs in brackets. This is what a person
    /// keys in when the symbol is scuffed, so it is a real fallback rather than
    /// decoration.
    /// </summary>
    public string HumanReadable() =>
        string.Concat(Elements.Select(element => $"({element.Ai}){element.Value}"));

    private static bool HasPredefinedLength(string ai, out int totalLength)
    {
        totalLength = 0;

        return ai.Length >= 2
               && Gs1ApplicationIdentifiers.TryGetPredefinedTotalLength(ai[..2], out totalLength);
    }
}

/// <summary>
/// Failures from building an element string. Coded like every other error in the
/// system, because these reach an API caller editing a label template.
/// </summary>
public static class Gs1Errors
{
    public static readonly Error NoElements =
        Error.Validation("gs1.no_elements", "A GS1 element string needs at least one application identifier.");

    public static Error UnknownAi(string ai) =>
        Error.Validation("gs1.unknown_ai", $"Application identifier '{ai}' is not one this system knows how to encode.");

    public static Error DuplicateAi(string ai) =>
        Error.Validation("gs1.duplicate_ai", $"Application identifier '{ai}' appears twice in one element string.");

    public static Error EmptyValue(string ai) =>
        Error.Validation("gs1.empty_value", $"Application identifier '{ai}' was asked for with no value behind it.");

    public static Error ValueTooLong(string ai, int maximum) =>
        Error.Validation("gs1.value_too_long", $"Application identifier '{ai}' holds at most {maximum} characters.");

    public static Error NonNumeric(string ai) =>
        Error.Validation("gs1.non_numeric", $"Application identifier '{ai}' is numeric and was given something that is not.");

    public static Error WrongFixedLength(string ai, int required) =>
        Error.Validation(
            "gs1.wrong_fixed_length",
            $"Application identifier '{ai}' has a predefined length and needs exactly {required} characters.");
}
