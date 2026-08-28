using System.Globalization;
using Countflow.Modules.Labels.Binding;
using Countflow.Modules.Labels.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Labels.Kinds;

/// <summary>
/// Gathers everything a pallet label says, from the four modules that know it.
/// <para>
/// Four published contracts and no project references: the plate from
/// <see cref="ILicensePlateRegistry"/>, its contents from <see cref="IStockQuery"/>,
/// the item and its packaging from <see cref="IItemDirectory"/>, the site from
/// <see cref="IWarehouseDirectory"/>. This class is the entire answer to "does the
/// caller assemble the binding, or does the generator ask?" — and the reason it is
/// the generator is that the answer to "which GTIN goes on a pallet?" is subtle
/// enough to be worth having in one place.
/// </para>
/// </summary>
public sealed class PalletLabelBinder(
    ILicensePlateRegistry plates,
    IStockQuery stock,
    IItemDirectory items,
    ILocationDirectory locations,
    IWarehouseDirectory warehouses,
    TimeProvider clock)
{
    /// <summary>
    /// Binds a pallet label for one SSCC.
    /// <para>
    /// Three shapes come out of here and the difference between them is the point
    /// of the whole class. An <b>empty</b> plate — labelled, not yet built — gets
    /// its identity and nothing else. A <b>single</b> plate gets everything. A
    /// <b>mixed</b> plate gets its identity and is told to say "mixed": there is no
    /// one SKU, no one batch and no one expiry, and a label that printed the first
    /// of several would be a label asserting something the ledger does not.
    /// </para>
    /// </summary>
    public async Task<Result<LabelBinding>> BindAsync(
        string sscc,
        Guid warehouseId,
        bool createPlate,
        CancellationToken cancellationToken)
    {
        if (!Sscc.TryParse(sscc, out var normalised))
        {
            return Result.Failure<LabelBinding>(LabelsErrors.InvalidSscc);
        }

        var site = await warehouses.FindAsync(warehouseId, cancellationToken);

        if (site is null)
        {
            return Result.Failure<LabelBinding>(LabelsErrors.WarehouseNotFound);
        }

        LicensePlateSnapshot? plate;

        if (createPlate)
        {
            // Printing the label is the moment the plate becomes real — there is
            // now an object in the building carrying this number. See
            // ILicensePlateRegistry.
            var ensured = await plates.EnsureAsync(normalised, cancellationToken);
            if (ensured.IsFailure) return Result.Failure<LabelBinding>(ensured.Error);

            plate = ensured.Value;
        }
        else
        {
            plate = await plates.FindAsync(normalised, cancellationToken);
        }

        var holdings = plate is null
            ? []
            : await stock.PlateHoldingsAsync(plate.Id, cancellationToken);

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [PalletLabelFields.Sscc] = normalised,
            [PalletLabelFields.SsccHumanReadable] = FormatSscc(normalised),
            [PalletLabelFields.WarehouseCode] = site.Code,
            [PalletLabelFields.WarehouseName] = site.Name,
            [PalletLabelFields.PrintedAt] = clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            [PalletLabelFields.Content] = "empty",
            [PalletLabelFields.Sku] = null,
            [PalletLabelFields.Description] = null,
            [PalletLabelFields.LotNumber] = null,
            [PalletLabelFields.ExpiryDate] = null,
            [PalletLabelFields.Quantity] = null,
            [PalletLabelFields.BaseUom] = null,
            [PalletLabelFields.CaseCount] = null,
            [PalletLabelFields.CaseGtin] = null,
            [PalletLabelFields.LocationCode] = null
        };

        // AI 00 first and always. It is the pallet's identity, it is what makes the
        // label a licence plate rather than a picture of one, and it is the only
        // element that survives every one of the three shapes below.
        var gs1 = new List<(string Ai, string Value)> { ("00", normalised) };

        var distinctItems = holdings.Select(h => h.ItemId).Distinct().ToList();
        var distinctLots = holdings.Select(h => h.LotId).Distinct().ToList();

        if (holdings.Count == 0)
        {
            return Build(fields, gs1, warehouseId, plate?.Id);
        }

        if (distinctItems.Count > 1 || distinctLots.Count > 1)
        {
            fields[PalletLabelFields.Content] = "mixed";

            // Nothing else. Not the total quantity either: a number with no unit
            // and no SKU beside it is the kind of figure somebody writes down.
            return Build(fields, gs1, warehouseId, plate?.Id);
        }

        var item = await items.FindAsync(distinctItems[0], cancellationToken);

        if (item is null)
        {
            // The plate holds stock of an item the catalogue no longer publishes.
            // Refused rather than half-printed: a pallet label with a blank
            // description is indistinguishable from one for an item whose
            // description is genuinely empty, and this is a data fault somebody
            // needs to see.
            return Result.Failure<LabelBinding>(LabelsErrors.ItemNotFound);
        }

        var quantity = holdings.Sum(holding => holding.Quantity);
        var lot = holdings[0].LotId;

        fields[PalletLabelFields.Content] = "single";
        fields[PalletLabelFields.Sku] = item.Sku;
        fields[PalletLabelFields.Description] = item.Description;
        fields[PalletLabelFields.Quantity] = Trim(quantity);
        fields[PalletLabelFields.BaseUom] = item.BaseUom;

        // One plate can legitimately be split across two bays, and the label says
        // where only when there is a single answer. See PlateHolding.
        var locationIds = holdings.Select(holding => holding.LocationId).Distinct().ToList();

        if (locationIds.Count == 1)
        {
            var location = await locations.FindAsync(locationIds[0], cancellationToken);
            fields[PalletLabelFields.LocationCode] = location?.Code;
        }

        if (lot is { } lotId)
        {
            var lots = await stock.FindLotsAsync([lotId], cancellationToken);

            if (lots.TryGetValue(lotId, out var batch))
            {
                fields[PalletLabelFields.LotNumber] = batch.LotNumber;

                if (batch.ExpiryDate is { } expiry)
                {
                    fields[PalletLabelFields.ExpiryDate] = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    // AI 17 is fixed-length YYMMDD, so it goes in ahead of the
                    // variable-length batch number below — one fewer separator, and
                    // the ordering is the builder's to preserve rather than invent.
                    gs1.Add(("17", expiry.ToString("yyMMdd", CultureInfo.InvariantCulture)));
                }

                gs1.Add(("10", batch.LotNumber));
            }
        }

        // The content block. AI 02 means "GTIN of the trade items contained", which
        // is the *case* GTIN and never the loose unit's — the domain note this
        // codebase keeps repeating, and the single most common way a logistics
        // label is wrong. AI 37 counts those same trade items, so the base-unit
        // quantity the ledger holds has to be divided by what a case contains.
        var packaging = await items.PackagingAsync(item.Id, cancellationToken);
        var tradeUnit = TradeUnit(packaging);

        if (tradeUnit is { Gtin: { } caseGtin, BaseUnitQuantity: > 0 } pack)
        {
            fields[PalletLabelFields.CaseGtin] = caseGtin;

            gs1.Insert(1, ("02", caseGtin));

            // Only when the pallet holds whole cases. A part-case pallet has no
            // honest AI 37 — the count is not an integer — and rounding it would
            // put a number in a barcode that a customer's system will believe.
            if (quantity % pack.BaseUnitQuantity == 0)
            {
                var cases = (int)(quantity / pack.BaseUnitQuantity);

                fields[PalletLabelFields.CaseCount] = cases.ToString(CultureInfo.InvariantCulture);
                gs1.Add(("37", cases.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return Build(fields, gs1, warehouseId, plate?.Id);
    }

    private static Result<LabelBinding> Build(
        Dictionary<string, string?> fields,
        List<(string Ai, string Value)> gs1,
        Guid warehouseId,
        Guid? plateId) =>
        new LabelBinding
        {
            Kind = LabelKind.Pallet,
            SubjectKey = fields[PalletLabelFields.Sscc]!,
            SubjectId = plateId,
            WarehouseId = warehouseId,
            Fields = fields,
            Gs1Elements = gs1
        };

    /// <summary>
    /// The packaging level a pallet's contents are counted in. The one flagged as
    /// the receiving unit if the catalogue says so, otherwise the Case rung —
    /// because the catalogue's own flag is a statement about this warehouse and the
    /// enum is only a name.
    /// </summary>
    private static PackSnapshot? TradeUnit(IReadOnlyList<PackSnapshot> packaging) =>
        packaging.FirstOrDefault(pack => pack.IsReceivingUnit && pack.Gtin is not null)
        ?? packaging.FirstOrDefault(pack => pack.Level == PackLevel.Case && pack.Gtin is not null);

    /// <summary>
    /// The SSCC in the groups the GS1 general specifications print it in, which is
    /// what makes eighteen digits keyable by a person at all.
    /// </summary>
    private static string FormatSscc(string sscc) =>
        $"{sscc[..1]} {sscc[1..8]} {sscc[8..17]} {sscc[17..]}";

    /// <summary>
    /// Quantities are decimal in the ledger and whole on nearly every label. Trailing
    /// zeroes are dropped so a pallet of 48 does not print as 48.0000.
    /// </summary>
    private static string Trim(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString(CultureInfo.InvariantCulture)
            : quantity.ToString("0.####", CultureInfo.InvariantCulture);
}
