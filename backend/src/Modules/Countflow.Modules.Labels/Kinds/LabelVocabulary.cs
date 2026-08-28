using Countflow.Modules.Labels.Binding;
using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Rendering;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Labels.Kinds;

/// <summary>
/// What each label kind's template may reference, and whether a given one would
/// actually print.
/// <para>
/// The one place the kinds are addressed generically, and deliberately the only
/// one — the same discipline <c>RuleVocabulary</c> keeps, for the same reason.
/// This switch exists for exactly two purposes: telling an editor which fields a
/// supervisor may drag onto a label, and answering "would this render?".
/// </para>
/// </summary>
public static class LabelVocabulary
{
    public static IReadOnlyList<LabelKind> Kinds { get; } = [LabelKind.Pallet];

    public static IReadOnlyList<LabelFieldDefinition> Fields(LabelKind kind) => kind switch
    {
        LabelKind.Pallet => PalletLabelFields.All,
        _ => []
    };

    public static bool HasVocabulary(LabelKind kind) => Fields(kind).Count > 0;

    /// <summary>
    /// Whether a template would print, checked by <b>rendering it</b> against a
    /// specimen rather than by a second set of validation rules written beside the
    /// renderer.
    /// <para>
    /// This is the same trick <c>RuleVocabulary.Builds</c> plays and it earns its
    /// place for the same reason: a validator written alongside the renderer drifts
    /// away from it, and the two only disagree in the cases nobody thought about.
    /// Running the real renderer cannot drift, because it <i>is</i> the renderer —
    /// an unknown field code, a GS1 barcode whose element string will not encode,
    /// a text element with neither a literal nor a field, an origin off the edge of
    /// the label: all of them fail here, at the moment somebody presses save.
    /// </para>
    /// <para>
    /// The specimen matters as much as the check. It is built with <b>every
    /// optional field absent</b> — no batch, no expiry, no case GTIN — because that
    /// is the pallet a template is most likely to break on and least likely to be
    /// designed against: the one labelled before it was built. A template that
    /// renders against the specimen renders against an empty pallet at a dock at
    /// six in the morning, which is the whole of what this promise is worth.
    /// </para>
    /// </summary>
    public static Result Renders(LabelTemplate template)
    {
        if (!HasVocabulary(template.Kind))
        {
            return Result.Failure(LabelsErrors.NoVocabulary(template.Kind.ToString()));
        }

        var rendered = ZplRenderer.Render(template, Specimen(template.Kind));

        return rendered.IsFailure ? Result.Failure(rendered.Error) : Result.Success();
    }

    /// <summary>
    /// A stand-in subject for checking a template. Every field the kind publishes
    /// is present as a key — a template referencing one that is missing is the
    /// failure this is here to catch — and every optional one is null.
    /// </summary>
    public static LabelBinding Specimen(LabelKind kind) => kind switch
    {
        LabelKind.Pallet => new LabelBinding
        {
            Kind = LabelKind.Pallet,
            SubjectKey = SpecimenSscc,
            WarehouseId = Guid.Empty,
            Fields = PalletLabelFields.All.ToDictionary(
                field => field.Code,
                field => field.IsAlwaysPresent ? SpecimenValue(field.Code) : null,
                StringComparer.Ordinal),
            // AI 00 alone, because that is what an unbuilt pallet's element string
            // holds and a template whose barcode needs more than the identity is a
            // template that cannot print one at a dock.
            Gs1Elements = [("00", SpecimenSscc)]
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No specimen for that label kind.")
    };

    /// <summary>
    /// A structurally valid SSCC that is obviously not a real one. The check digit
    /// holds, because the renderer's GS1 builder checks it and a specimen that
    /// failed validation would fail every template.
    /// </summary>
    private static string SpecimenSscc { get; } = SharedKernel.Gs1.Sscc.FromPayload("00000000000000000");

    private static string SpecimenValue(string code) => code switch
    {
        PalletLabelFields.Sscc => SpecimenSscc,
        PalletLabelFields.SsccHumanReadable => SpecimenSscc,
        PalletLabelFields.WarehouseCode => "WH0",
        PalletLabelFields.WarehouseName => "Specimen",
        PalletLabelFields.PrintedAt => "2026-01-01 00:00",
        PalletLabelFields.Content => "empty",
        _ => "specimen"
    };
}
