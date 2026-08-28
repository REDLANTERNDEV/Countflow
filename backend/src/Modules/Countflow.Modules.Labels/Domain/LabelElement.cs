namespace Countflow.Modules.Labels.Domain;

/// <summary>What one element of a template puts on the paper.</summary>
public enum LabelElementType
{
    /// <summary>
    /// Words. Either a literal caption the template supplies, or one field of the
    /// binding — exactly one of the two, never both and never neither.
    /// </summary>
    Text = 1,

    /// <summary>A symbol. See <see cref="BarcodeSymbology"/>.</summary>
    Barcode = 2,

    /// <summary>
    /// A rule or a box. Unbound and therefore uninteresting, and included because
    /// a logistics label without the three horizontal rules across it does not look
    /// like a logistics label, and operators read layout before they read words.
    /// </summary>
    Box = 3
}

/// <summary>
/// How a barcode element encodes what it is given.
/// <para>
/// The GS1 variants take the <i>whole element string</i> the kind's binder built —
/// they are not pointed at a field. That is deliberate and it is the sharpest
/// argument for a structured template over a ZPL blob: an element string needs
/// FNC1 in some positions and not others, and which positions depends on the AIs
/// present, so it cannot be a hole punched in a string somebody pasted out of a
/// label designer. Something has to know GS1, and the something is
/// <c>Gs1ElementString</c>.
/// </para>
/// </summary>
public enum BarcodeSymbology
{
    /// <summary>GS1-128. The workhorse of the logistics label.</summary>
    Gs1128 = 1,

    /// <summary>GS1 DataMatrix, for when the label is too small for a linear symbol.</summary>
    Gs1DataMatrix = 2,

    /// <summary>
    /// Plain Code 128 over one field, carrying no application identifiers. For a
    /// works order number or an internal reference — anything whose meaning is
    /// this warehouse's business and not the supply chain's.
    /// </summary>
    Code128 = 3
}

/// <summary>
/// One thing on a label, positioned in printer dots from the top left.
/// <para>
/// The type-specific columns are nullable and only some apply to each
/// <see cref="Type"/>, which is the same shape <c>RuleDefinition</c> uses for
/// <c>Weight</c> and <c>Parameters</c>. The alternative — a table per element type
/// — would buy a database-level guarantee about columns that are already checked
/// by rendering the template, and cost a join per type forever.
/// </para>
/// </summary>
public sealed class LabelElement
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid LabelTemplateId { get; init; }

    public required LabelElementType Type { get; set; }

    /// <summary>
    /// Draw order. Matters for overlap and for nothing else, but it matters
    /// enough: a box drawn after the text it frames erases the text.
    /// </summary>
    public int Order { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>Rotation in ZPL's own vocabulary: N, R, I, B. Defaults to N.</summary>
    public string Rotation { get; set; } = "N";

    /// <summary>
    /// The binding field this element prints, e.g. <c>pallet.sku</c>. Null on a
    /// literal text element, on a box, and on a GS1 barcode — which takes the whole
    /// element string rather than one field.
    /// <para>
    /// A code the kind's vocabulary does not know is a hard failure at render time,
    /// never a blank. Same argument as <c>RuleDefinition.Code</c>: silently
    /// dropping the field would print a pallet label with no batch number on it
    /// because somebody renamed something.
    /// </para>
    /// </summary>
    public string? FieldCode { get; set; }

    /// <summary>Fixed words. Set on a text element that is a caption rather than a value.</summary>
    public string? Literal { get; set; }

    /// <summary>Character height in dots. Text only.</summary>
    public int? FontHeight { get; set; }

    /// <summary>Character width in dots, or null to let the printer scale from the height.</summary>
    public int? FontWidth { get; set; }

    public BarcodeSymbology? Symbology { get; set; }

    /// <summary>Bar height in dots. Barcode only.</summary>
    public int? BarcodeHeight { get; set; }

    /// <summary>
    /// Whether to print the human-readable line under the symbol. Nearly always
    /// yes on a pallet label: it is what somebody keys in when the barcode is
    /// scuffed, which on a pallet that has been round a yard in the rain is often.
    /// </summary>
    public bool ShowInterpretation { get; set; } = true;

    /// <summary>Box width in dots. Box only.</summary>
    public int? WidthDots { get; set; }

    /// <summary>Box height in dots. Box only. A height of 1 is a horizontal rule.</summary>
    public int? HeightDots { get; set; }

    /// <summary>Line thickness in dots. Box only.</summary>
    public int? ThicknessDots { get; set; }

    public LabelTemplate? Template { get; init; }
}
