using Countflow.Modules.Labels;
using Countflow.Modules.Labels.Binding;
using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Kinds;
using Countflow.Modules.Labels.Rendering;
using Countflow.SharedKernel.Gs1;

namespace Countflow.IntegrationTests;

/// <summary>
/// The renderer on its own, with no database and no container — which is the whole
/// claim the issue behind this module made: generation is testable with no printer,
/// by asserting on the ZPL it emits.
/// <para>
/// These are the tests that decide whether a structured template was worth the
/// second language it costs. Every one of them asserts something a pasted ZPL blob
/// with holes punched in it could not have been checked for.
/// </para>
/// </summary>
public sealed class ZplRendererTests
{
    private static string Sscc18 => Sscc.FromPayload("30512345000000001");

    // ------------------------------------------------------------ the output

    [Fact]
    public void A_rendered_label_opens_and_closes_and_declares_its_extent()
    {
        var zpl = Render(Template(Text(0, PalletLabelFields.Sku)), Binding());

        Assert.StartsWith("^XA", zpl, StringComparison.Ordinal);
        Assert.EndsWith("^XZ", zpl, StringComparison.Ordinal);
        Assert.Contains("^PW812", zpl, StringComparison.Ordinal);
        Assert.Contains("^LL1218", zpl, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not decoration. Descriptions here are whatever the catalogue holds and the
    /// console runs in Turkish as well as English; a printer left on its default
    /// code page renders those as mojibake, on a label, silently.
    /// </summary>
    [Fact]
    public void Every_label_declares_utf8()
    {
        Assert.Contains("^CI28", Render(Template(Text(0, PalletLabelFields.Sku)), Binding()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_element_prints_the_bound_value()
    {
        var zpl = Render(
            Template(Text(0, PalletLabelFields.Sku)),
            Binding(sku: "WIDGET-1"));

        Assert.Contains("WIDGET-1", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_element_prints_its_caption()
    {
        var element = Text(0, field: null);
        element.Literal = "BATCH";

        Assert.Contains("BATCH", Render(Template(element), Binding()), StringComparison.Ordinal);
    }

    // ------------------------------------------------- the escaping argument

    /// <summary>
    /// <b>The single strongest argument for rendering rather than interpolating.</b>
    /// A caret in an item description is a ZPL command. In a blob with a hole in it
    /// this does not throw and does not look wrong — it prints a label missing
    /// everything after the caret, and nobody finds out until a customer rejects a
    /// pallet.
    /// </summary>
    [Theory]
    [InlineData("WIDGET ^ BRACKET", "_5E")]
    [InlineData("WIDGET ~ BRACKET", "_7E")]
    [InlineData("WIDGET \\ BRACKET", "_5C")]
    [InlineData("WIDGET _ BRACKET", "_5F")]
    public void Zpl_control_characters_in_a_value_are_hex_escaped(string description, string escape)
    {
        var zpl = Render(
            Template(Text(0, PalletLabelFields.Description)),
            Binding(description: description));

        // Hex escaping is on for the field, and the control character is gone.
        Assert.Contains("^FH\\", zpl, StringComparison.Ordinal);
        Assert.Contains(escape, zpl, StringComparison.Ordinal);
        Assert.DoesNotContain($"^FD{description}", zpl, StringComparison.Ordinal);
    }

    // --------------------------------------------------------- the GS1 barcode

    [Fact]
    public void A_gs1_barcode_encodes_the_bindings_element_string()
    {
        var zpl = Render(Template(Gs1Barcode(0)), Binding());

        Assert.Contains("^BCN,100,Y,N,N,A", zpl, StringComparison.Ordinal);
        Assert.Contains("00" + Sscc18, zpl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The separator is the whole difficulty, and it is why a GS1 barcode cannot be
    /// a placeholder in a pasted string: where FNC1 goes depends on which AIs are
    /// present, so nothing but a GS1-aware builder can place it.
    /// </summary>
    [Fact]
    public void A_gs1_barcode_places_fnc1_only_after_variable_length_elements()
    {
        var binding = Binding(gs1:
        [
            ("00", Sscc18),
            ("17", "270131"),
            ("10", "LOT-9"),
            ("37", "48")
        ]);

        var zpl = Render(Template(Gs1Barcode(0)), binding);

        Assert.Contains($"00{Sscc18}17270131" + "10LOT-9>8" + "3748", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gs1_datamatrix_uses_the_ecc_level_gs1_requires()
    {
        var element = Gs1Barcode(0);
        element.Symbology = BarcodeSymbology.Gs1DataMatrix;
        element.BarcodeHeight = 6;

        Assert.Contains("^BXN,6,200", Render(Template(element), Binding()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------- absence vs error

    /// <summary>
    /// A pallet labelled before it was built has no batch number, and the label has
    /// to print without one rather than refusing. The position stays occupied so a
    /// layout does not reflow.
    /// </summary>
    [Fact]
    public void A_field_with_no_value_prints_as_nothing_rather_than_failing()
    {
        var rendered = ZplRenderer.Render(
            Template(Text(0, PalletLabelFields.LotNumber)),
            Binding(lot: null));

        Assert.True(rendered.IsSuccess);
        Assert.Contains("^FD^FS", rendered.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other half of that distinction: a field the kind does not publish is
    /// a hard failure, never a blank. Silently dropping it prints a pallet label
    /// with no batch number because somebody renamed something.
    /// </summary>
    [Fact]
    public void A_field_the_kind_does_not_publish_is_refused()
    {
        var rendered = ZplRenderer.Render(
            Template(Text(0, "pallet.invented_yesterday")),
            Binding());

        Assert.True(rendered.IsFailure);
        Assert.Equal("labels.unknown_field", rendered.Error.Code);
    }

    /// <summary>
    /// A plain Code 128 over an absent field would encode an empty string — a
    /// barcode that scans as nothing while looking like a barcode, which is worse
    /// than no barcode at all.
    /// </summary>
    [Fact]
    public void A_code128_barcode_over_an_absent_field_is_refused()
    {
        var element = Gs1Barcode(0);
        element.Symbology = BarcodeSymbology.Code128;
        element.FieldCode = PalletLabelFields.LotNumber;

        var rendered = ZplRenderer.Render(Template(element), Binding(lot: null));

        Assert.True(rendered.IsFailure);
        Assert.Equal("labels.barcode_field_empty", rendered.Error.Code);
    }

    // ------------------------------------------------------------- geometry

    [Fact]
    public void An_element_starting_off_the_label_is_refused()
    {
        var element = Text(0, PalletLabelFields.Sku);
        element.X = 9000;

        var rendered = ZplRenderer.Render(Template(element), Binding());

        Assert.True(rendered.IsFailure);
        Assert.Equal("labels.element_off_label", rendered.Error.Code);
    }

    [Fact]
    public void An_unknown_rotation_is_refused()
    {
        var element = Text(0, PalletLabelFields.Sku);
        element.Rotation = "Z";

        Assert.Equal("labels.invalid_rotation", ZplRenderer.Render(Template(element), Binding()).Error.Code);
    }

    [Fact]
    public void A_text_element_with_neither_a_literal_nor_a_field_is_refused()
    {
        var rendered = ZplRenderer.Render(Template(Text(0, field: null)), Binding());

        Assert.True(rendered.IsFailure);
        Assert.Equal("labels.text_has_nothing", rendered.Error.Code);
    }

    [Fact]
    public void Elements_are_emitted_in_draw_order()
    {
        var first = Text(1, PalletLabelFields.Sku);
        var second = Text(0, PalletLabelFields.Description);

        var zpl = Render(Template(first, second), Binding(sku: "SKU-A", description: "DESC-B"));

        Assert.True(
            zpl.IndexOf("DESC-B", StringComparison.Ordinal) < zpl.IndexOf("SKU-A", StringComparison.Ordinal),
            "draw order decides overlap, so it has to decide emission order");
    }

    // ------------------------------------------- validation by rendering

    /// <summary>
    /// The <c>RuleVocabulary.Builds</c> trick, applied to labels: a template is
    /// checked by running the real renderer over a specimen, so the check cannot
    /// drift away from what will actually happen.
    /// </summary>
    [Fact]
    public void A_template_is_validated_by_rendering_it_against_a_specimen()
    {
        Assert.True(LabelVocabulary.Renders(Template(Text(0, PalletLabelFields.Sku))).IsSuccess);

        var broken = LabelVocabulary.Renders(Template(Text(0, "pallet.nonsense")));

        Assert.True(broken.IsFailure);
        Assert.Equal("labels.unknown_field", broken.Error.Code);
    }

    /// <summary>
    /// The specimen carries every optional field as absent, so a layout is checked
    /// against the pallet it is most likely to break on: the one labelled before it
    /// was built.
    /// </summary>
    [Fact]
    public void The_specimen_has_no_contents()
    {
        var specimen = LabelVocabulary.Specimen(LabelKind.Pallet);

        Assert.Null(specimen.Fields[PalletLabelFields.LotNumber]);
        Assert.Null(specimen.Fields[PalletLabelFields.Sku]);
        Assert.NotNull(specimen.Fields[PalletLabelFields.Sscc]);
        Assert.Equal(("00", specimen.SubjectKey), specimen.Gs1Elements.Single());
    }

    /// <summary>
    /// Every field the vocabulary publishes has to be a key in the specimen, or a
    /// template using it would be refused at save time for a fault in this module
    /// rather than in the template.
    /// </summary>
    [Fact]
    public void The_specimen_answers_for_every_published_field()
    {
        var specimen = LabelVocabulary.Specimen(LabelKind.Pallet);

        Assert.All(
            LabelVocabulary.Fields(LabelKind.Pallet),
            field => Assert.True(
                specimen.Fields.ContainsKey(field.Code),
                $"{field.Code} is published but the specimen cannot answer for it"));
    }

    [Fact]
    public void An_empty_template_is_refused()
    {
        var template = Template();

        Assert.Equal("labels.template_empty", ZplRenderer.Render(template, Binding()).Error.Code);
    }

    // --------------------------------------------------------------- helpers

    private static string Render(LabelTemplate template, LabelBinding binding)
    {
        var rendered = ZplRenderer.Render(template, binding);

        Assert.True(rendered.IsSuccess, rendered.IsFailure ? rendered.Error.Description : string.Empty);

        return rendered.Value;
    }

    private static LabelTemplate Template(params LabelElement[] elements)
    {
        var template = new LabelTemplate
        {
            Kind = LabelKind.Pallet,
            Name = "Test",
            Version = 1,
            WidthDots = 812,
            HeightDots = 1218
        };

        foreach (var element in elements)
        {
            template.Elements.Add(element);
        }

        return template;
    }

    private static LabelElement Text(int order, string? field) =>
        new()
        {
            LabelTemplateId = Guid.Empty,
            Type = LabelElementType.Text,
            Order = order,
            X = 30,
            Y = 30 + (order * 40),
            FieldCode = field,
            FontHeight = 30
        };

    private static LabelElement Gs1Barcode(int order) =>
        new()
        {
            LabelTemplateId = Guid.Empty,
            Type = LabelElementType.Barcode,
            Order = order,
            X = 30,
            Y = 400,
            Symbology = BarcodeSymbology.Gs1128,
            BarcodeHeight = 100,
            ShowInterpretation = true
        };

    private static LabelBinding Binding(
        string? sku = "SKU-1",
        string? description = "A test item",
        string? lot = "LOT-1",
        IReadOnlyList<(string Ai, string Value)>? gs1 = null)
    {
        var fields = LabelVocabulary.Fields(LabelKind.Pallet)
            .ToDictionary(field => field.Code, _ => (string?)null, StringComparer.Ordinal);

        fields[PalletLabelFields.Sscc] = Sscc18;
        fields[PalletLabelFields.SsccHumanReadable] = Sscc18;
        fields[PalletLabelFields.WarehouseCode] = "WH1";
        fields[PalletLabelFields.WarehouseName] = "Test DC";
        fields[PalletLabelFields.PrintedAt] = "2026-08-28 09:00";
        fields[PalletLabelFields.Content] = "single";
        fields[PalletLabelFields.Sku] = sku;
        fields[PalletLabelFields.Description] = description;
        fields[PalletLabelFields.LotNumber] = lot;

        return new LabelBinding
        {
            Kind = LabelKind.Pallet,
            SubjectKey = Sscc18,
            WarehouseId = Guid.Empty,
            Fields = fields,
            Gs1Elements = gs1 ?? [("00", Sscc18)]
        };
    }
}
