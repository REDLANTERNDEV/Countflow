using System.Globalization;
using System.Text;
using Countflow.Modules.Labels.Binding;
using Countflow.Modules.Labels.Domain;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Labels.Rendering;

/// <summary>
/// Turns a template and a binding into ZPL II.
/// <para>
/// <b>Why a structured template renders to ZPL, rather than a ZPL string being the
/// template.</b> A pasted blob out of a label designer with <c>${sscc}</c> holes in
/// it is cheaper by a wide margin, and it is untestable past "a string came out".
/// Three things decided it the other way. A GS1 barcode's data needs FNC1 in some
/// positions and not others depending on which AIs are present, so a hole in a
/// blob cannot be filled correctly without re-parsing the designer's output.
/// Values have to be escaped — an item description containing a caret is a ZPL
/// command, and in a blob it silently truncates the label. And a template that can
/// be rendered can be rendered against a specimen, which is how a supervisor finds
/// out their layout is broken at the moment they save it rather than at a rack.
/// </para>
/// <para>
/// The cost is real and worth stating: this is a second language nobody asked for,
/// and a warehouse that wants a design their label software produced cannot paste
/// it in. That is the trade, and it is recorded in
/// <c>docs/decisions.md</c>.
/// </para>
/// </summary>
public static class ZplRenderer
{
    /// <summary>Rotations ZPL understands: normal, rotated, inverted, bottom-up.</summary>
    private static readonly string[] Rotations = ["N", "R", "I", "B"];

    public static Result<string> Render(LabelTemplate template, LabelBinding binding)
    {
        if (template.Kind != binding.Kind)
        {
            return Result.Failure<string>(LabelsErrors.BindingKindMismatch);
        }

        if (template.WidthDots <= 0 || template.HeightDots <= 0)
        {
            return Result.Failure<string>(LabelsErrors.TemplateHasNoExtent);
        }

        if (template.Elements.Count == 0)
        {
            return Result.Failure<string>(LabelsErrors.TemplateEmpty);
        }

        var zpl = new StringBuilder();

        zpl.Append("^XA");

        // UTF-8, and not optional. Descriptions in this system are whatever the
        // catalogue holds, the console runs in Turkish as well as English, and a
        // printer left on its default code page renders those as mojibake — on a
        // label, silently, forever.
        zpl.Append("^CI28");

        zpl.Append(CultureInfo.InvariantCulture, $"^PW{template.WidthDots}");
        zpl.Append(CultureInfo.InvariantCulture, $"^LL{template.HeightDots}");
        zpl.Append("^LH0,0");

        if (template.Darkness is { } darkness)
        {
            zpl.Append(CultureInfo.InvariantCulture, $"^MD{darkness}");
        }

        foreach (var element in template.Elements.OrderBy(e => e.Order).ThenBy(e => e.Id))
        {
            var appended = Append(zpl, element, template, binding);

            if (appended.IsFailure)
            {
                return Result.Failure<string>(appended.Error);
            }
        }

        zpl.Append("^XZ");

        return zpl.ToString();
    }

    private static Result Append(
        StringBuilder zpl,
        LabelElement element,
        LabelTemplate template,
        LabelBinding binding)
    {
        if (element.X < 0 || element.Y < 0
            || element.X >= template.WidthDots || element.Y >= template.HeightDots)
        {
            // Only the origin is checked, because the extent of a text element
            // depends on font metrics this code does not have. An origin off the
            // label is unambiguous, though: nothing that starts there can appear.
            return Result.Failure(LabelsErrors.ElementOffLabel(element.Order));
        }

        if (!Rotations.Contains(element.Rotation, StringComparer.Ordinal))
        {
            return Result.Failure(LabelsErrors.InvalidRotation(element.Rotation));
        }

        zpl.Append(CultureInfo.InvariantCulture, $"^FO{element.X},{element.Y}");

        return element.Type switch
        {
            LabelElementType.Text => AppendText(zpl, element, binding),
            LabelElementType.Barcode => AppendBarcode(zpl, element, binding),
            LabelElementType.Box => AppendBox(zpl, element),
            _ => Result.Failure(LabelsErrors.UnknownElementType(element.Type.ToString()))
        };
    }

    private static Result AppendText(StringBuilder zpl, LabelElement element, LabelBinding binding)
    {
        if (element.Literal is not null && element.FieldCode is not null)
        {
            return Result.Failure(LabelsErrors.TextIsBothLiteralAndField(element.Order));
        }

        string? value;

        if (element.FieldCode is { } code)
        {
            var resolved = Resolve(code, binding);
            if (resolved.IsFailure) return Result.Failure(resolved.Error);

            value = resolved.Value;
        }
        else if (element.Literal is not null)
        {
            value = element.Literal;
        }
        else
        {
            return Result.Failure(LabelsErrors.TextHasNothingToPrint(element.Order));
        }

        var height = element.FontHeight ?? 30;
        var width = element.FontWidth ?? 0;

        zpl.Append(CultureInfo.InvariantCulture, $"^A0{element.Rotation},{height},{width}");

        // A field whose value is null prints as nothing rather than as the word
        // "null" or as a skipped element — the position stays occupied, so a
        // layout does not reflow because a pallet had no batch number.
        AppendData(zpl, value ?? string.Empty);

        return Result.Success();
    }

    private static Result AppendBarcode(StringBuilder zpl, LabelElement element, LabelBinding binding)
    {
        if (element.Symbology is not { } symbology)
        {
            return Result.Failure(LabelsErrors.BarcodeHasNoSymbology(element.Order));
        }

        var height = element.BarcodeHeight ?? 100;
        var interpretation = element.ShowInterpretation ? "Y" : "N";

        if (symbology == BarcodeSymbology.Code128)
        {
            if (element.FieldCode is not { } code)
            {
                return Result.Failure(LabelsErrors.BarcodeHasNoField(element.Order));
            }

            var resolved = Resolve(code, binding);
            if (resolved.IsFailure) return Result.Failure(resolved.Error);

            if (string.IsNullOrEmpty(resolved.Value))
            {
                // A plain Code 128 over an absent field would print a symbol
                // encoding an empty string — a barcode that scans as nothing,
                // which is worse than no barcode, because it looks like one.
                return Result.Failure(LabelsErrors.BarcodeFieldEmpty(code));
            }

            zpl.Append(CultureInfo.InvariantCulture, $"^BC{element.Rotation},{height},{interpretation},N,N,N");
            AppendData(zpl, resolved.Value);

            return Result.Success();
        }

        // The GS1 variants take the whole element string the binder assembled, and
        // the builder — not this code — decides where the separators go.
        var built = Gs1ElementString.Build(binding.Gs1Elements);

        if (built.IsFailure)
        {
            return Result.Failure(built.Error);
        }

        var data = built.Value.Encode(Gs1ElementString.ZplFunction1);

        if (symbology == BarcodeSymbology.Gs1DataMatrix)
        {
            // ^BX takes a module size rather than a bar height, and quality 200 is
            // the ECC level GS1 requires for DataMatrix.
            var module = element.BarcodeHeight ?? 6;

            zpl.Append(CultureInfo.InvariantCulture, $"^BX{element.Rotation},{module},200");
        }
        else
        {
            // Mode A: the printer picks the subsets. Hand-rolling the subset
            // switches would mean this code deciding where to move between B and C
            // for every batch number a supplier ever invents, and getting one wrong
            // produces a symbol that scans as the wrong characters.
            zpl.Append(CultureInfo.InvariantCulture, $"^BC{element.Rotation},{height},{interpretation},N,N,A");
        }

        AppendData(zpl, data);

        return Result.Success();
    }

    private static Result AppendBox(StringBuilder zpl, LabelElement element)
    {
        var width = element.WidthDots ?? 0;
        var height = element.HeightDots ?? 0;
        var thickness = element.ThicknessDots ?? 1;

        if (width <= 0 && height <= 0)
        {
            return Result.Failure(LabelsErrors.BoxHasNoExtent(element.Order));
        }

        zpl.Append(CultureInfo.InvariantCulture, $"^GB{width},{height},{thickness}^FS");

        return Result.Success();
    }

    private static Result<string?> Resolve(string code, LabelBinding binding)
    {
        // Absent from the binding is an unknown field, and it is a hard failure —
        // never a blank. Same argument as an unrecognised rule code: silently
        // dropping it prints a pallet label with no batch number on it because
        // somebody renamed something.
        if (!binding.Fields.TryGetValue(code, out var value))
        {
            return Result.Failure<string?>(LabelsErrors.UnknownField(code, binding.Kind.ToString()));
        }

        return Result.Success(value);
    }

    /// <summary>
    /// Writes a field's data, hex-escaped.
    /// <para>
    /// <b>This is the routine a ZPL blob cannot have.</b> <c>^</c>, <c>~</c> and
    /// <c>\</c> are ZPL's own control characters, so an item description reading
    /// "WIDGET ^ BRACKET" ends the field and runs the rest as commands. It does not
    /// throw and it does not print wrong-looking output — it prints a label that is
    /// missing everything after the caret. <c>^FH</c> turns on hex escaping and each
    /// of them goes out as <c>_XX</c>; the underscore itself has to be escaped first
    /// or it would be read as the start of an escape.
    /// </para>
    /// </summary>
    private static void AppendData(StringBuilder zpl, string value)
    {
        var escaped = value
            .Replace("_", "_5F", StringComparison.Ordinal)
            .Replace("^", "_5E", StringComparison.Ordinal)
            .Replace("~", "_7E", StringComparison.Ordinal)
            .Replace("\\", "_5C", StringComparison.Ordinal);

        zpl.Append("^FH\\^FD").Append(escaped).Append("^FS");
    }
}
