using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Kinds;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Persistence;

/// <summary>
/// Seeds the default pallet template.
/// <para>
/// Reference data a module cannot function without, in the same sense the rules
/// seeder's default rule sets are: a warehouse with no active pallet template
/// cannot print, and "promote a template before you can label a pallet" is not a
/// first-run experience worth defending. What is seeded is a plain 4×6 GS1
/// logistics label at 203 dpi — the stock nearly every distribution centre already
/// has on the printer.
/// </para>
/// <para>
/// Seeded with <c>WarehouseId</c> null, so it covers every site and is superseded
/// by a site-specific one the moment somebody promotes one. Idempotent by the same
/// test every seeder uses: if a template of this kind exists at all, this has run.
/// </para>
/// </summary>
public sealed class LabelsSeeder(LabelsDbContext dbContext, TimeProvider clock) : IModuleSeeder
{
    /// <summary>After MasterData, before nothing. Labels depends on no other seed.</summary>
    public int Order => 60;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.LabelTemplates.AnyAsync(t => t.Kind == LabelKind.Pallet, cancellationToken))
        {
            return;
        }

        var template = new LabelTemplate
        {
            Kind = LabelKind.Pallet,
            Name = "Default pallet label",
            Version = 1,
            Status = LabelTemplateStatus.Active,
            WarehouseId = null,
            // 4 × 6 inches at 203 dpi, which is what a ZT230 and its descendants
            // ship with. Dots rather than millimetres — see LabelTemplate.
            WidthDots = 812,
            HeightDots = 1218,
            CreatedAt = clock.GetUtcNow(),
            ActivatedAt = clock.GetUtcNow(),
            Notes = "Seeded. Clone it rather than editing it — an active template is not editable."
        };

        var elements = new List<LabelElement>
        {
            Text(template.Id, 0, 30, 30, PalletLabelFields.WarehouseName, height: 40),
            Rule(template.Id, 1, 30, 85, 750),

            Caption(template.Id, 2, 30, 110, "SKU", 26),
            Text(template.Id, 3, 30, 145, PalletLabelFields.Sku, height: 44),

            Caption(template.Id, 4, 30, 205, "DESCRIPTION", 26),
            Text(template.Id, 5, 30, 240, PalletLabelFields.Description, height: 34),

            Caption(template.Id, 6, 30, 305, "BATCH", 26),
            Text(template.Id, 7, 30, 340, PalletLabelFields.LotNumber, height: 40),

            Caption(template.Id, 8, 430, 305, "EXPIRY", 26),
            Text(template.Id, 9, 430, 340, PalletLabelFields.ExpiryDate, height: 40),

            Caption(template.Id, 10, 30, 400, "QUANTITY", 26),
            Text(template.Id, 11, 30, 435, PalletLabelFields.Quantity, height: 44),

            Caption(template.Id, 12, 430, 400, "CASES", 26),
            Text(template.Id, 13, 430, 435, PalletLabelFields.CaseCount, height: 44),

            Rule(template.Id, 14, 30, 505, 750),

            // The SSCC block, bottom third, which is where a forklift driver's eye
            // and a fixed scanner both expect it on a GS1 logistics label.
            Caption(template.Id, 15, 30, 530, "SSCC", 26),
            new LabelElement
            {
                LabelTemplateId = template.Id,
                Type = LabelElementType.Barcode,
                Order = 16,
                X = 60,
                Y = 570,
                Symbology = BarcodeSymbology.Gs1128,
                BarcodeHeight = 180,
                ShowInterpretation = true
            },

            Text(template.Id, 17, 30, 800, PalletLabelFields.SsccHumanReadable, height: 36),
            Text(template.Id, 18, 30, 860, PalletLabelFields.LocationCode, height: 30),
            Text(template.Id, 19, 30, 900, PalletLabelFields.PrintedAt, height: 24)
        };

        foreach (var element in elements)
        {
            template.Elements.Add(element);
        }

        dbContext.LabelTemplates.Add(template);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LabelElement Text(Guid templateId, int order, int x, int y, string field, int height) =>
        new()
        {
            LabelTemplateId = templateId,
            Type = LabelElementType.Text,
            Order = order,
            X = x,
            Y = y,
            FieldCode = field,
            FontHeight = height
        };

    private static LabelElement Caption(Guid templateId, int order, int x, int y, string literal, int height) =>
        new()
        {
            LabelTemplateId = templateId,
            Type = LabelElementType.Text,
            Order = order,
            X = x,
            Y = y,
            Literal = literal,
            FontHeight = height
        };

    private static LabelElement Rule(Guid templateId, int order, int x, int y, int width) =>
        new()
        {
            LabelTemplateId = templateId,
            Type = LabelElementType.Box,
            Order = order,
            X = x,
            Y = y,
            WidthDots = width,
            HeightDots = 1,
            ThicknessDots = 2
        };
}
