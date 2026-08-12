using System.Globalization;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Import;

/// <summary>
/// Bulk item import.
/// <para>
/// Two rules drive the design, both learned from how WMS rollouts fail. First,
/// the whole file is validated before anything is written — a half-loaded
/// catalogue is worse than no catalogue, because nobody can tell which half is
/// real. Second, dry run is the default posture: you see exactly what would
/// change before it changes.
/// </para>
/// </summary>
public static class ImportItems
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/import/items", async (
                HttpRequest httpRequest,
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken,
                bool dryRun = true) =>
            {
                using var reader = new StreamReader(httpRequest.Body);

                var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    // Suppliers send whatever column order they like, and a
                    // missing optional column should not abort the file.
                    HeaderValidated = null,
                    MissingFieldFound = null,
                    TrimOptions = TrimOptions.Trim,
                    PrepareHeaderForMatch = args => args.Header.Replace(" ", string.Empty).ToLowerInvariant()
                };

                using var csv = new CsvReader(reader, configuration);

                List<ItemImportRow> rows;
                try
                {
                    rows = await csv.GetRecordsAsync<ItemImportRow>(cancellationToken).ToListAsync(cancellationToken);
                }
                catch (CsvHelperException exception)
                {
                    return Result.Failure<ImportResult>(Error.Validation(
                        "import.malformed_csv",
                        $"The file could not be read as CSV: {exception.Message}")).ToHttpResult();
                }

                var result = await ProcessAsync(rows, dbContext, dryRun, cancellationToken);
                return Results.Ok(result);
            })
            .RequireAuthorization()
            .Accepts<string>("text/csv")
            .WithTags("Import")
            .WithName("ImportItems")
            .WithSummary("Import items from CSV. Validates the whole file first; dry run by default.")
            .Produces<ImportResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<ImportResult> ProcessAsync(
        List<ItemImportRow> rows,
        MasterDataDbContext dbContext,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var issues = new List<ImportIssue>();

        var skus = rows.Select(r => r.Sku).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        var existing = await dbContext.Items
            .Where(i => skus.Contains(i.Sku))
            .Include(i => i.PackConfigurations)
            .ToDictionaryAsync(i => i.Sku, cancellationToken);

        // Duplicates inside the file itself are a common spreadsheet accident
        // and would otherwise surface as an opaque unique-index violation.
        var duplicates = rows
            .Select((row, index) => (row, line: index + 2))
            .GroupBy(x => x.row.Sku, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);

        foreach (var group in duplicates)
        {
            foreach (var (_, line) in group.Skip(1))
            {
                issues.Add(new ImportIssue(line, nameof(ItemImportRow.Sku),
                    $"SKU '{group.Key}' appears more than once in this file."));
            }
        }

        var toCreate = new List<Item>();
        var updates = 0;
        var unchanged = 0;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var line = index + 2; // header is line 1
            var rowIssues = issues.Count;

            if (string.IsNullOrWhiteSpace(row.Sku))
            {
                issues.Add(new ImportIssue(line, nameof(row.Sku), "SKU is required."));
            }

            if (string.IsNullOrWhiteSpace(row.Description))
            {
                issues.Add(new ImportIssue(line, nameof(row.Description), "Description is required."));
            }

            AbcClass abc = AbcClass.C;
            if (!string.IsNullOrWhiteSpace(row.AbcClass) && !Enum.TryParse(row.AbcClass, true, out abc))
            {
                issues.Add(new ImportIssue(line, nameof(row.AbcClass),
                    $"'{row.AbcClass}' is not a valid ABC class (A, B or C)."));
            }

            TemperatureClass temperature = TemperatureClass.Ambient;
            if (!string.IsNullOrWhiteSpace(row.TemperatureClass)
                && !Enum.TryParse(row.TemperatureClass, true, out temperature))
            {
                issues.Add(new ImportIssue(line, nameof(row.TemperatureClass),
                    $"'{row.TemperatureClass}' is not a valid temperature class."));
            }

            string? normalisedGtin = null;
            if (!string.IsNullOrWhiteSpace(row.CaseGtin))
            {
                if (Gtin.TryParse(row.CaseGtin, out var gtin))
                {
                    normalisedGtin = gtin.Value.Value;
                }
                else
                {
                    // Rejecting a bad check digit here is the entire point: a
                    // mistyped barcode that reaches the catalogue means a scan
                    // on the floor silently matches the wrong product.
                    issues.Add(new ImportIssue(line, nameof(row.CaseGtin),
                        $"'{row.CaseGtin}' is not a valid GTIN (check digit failed)."));
                }
            }

            if (row.CaseQuantity is <= 0)
            {
                issues.Add(new ImportIssue(line, nameof(row.CaseQuantity),
                    "Case quantity must be greater than zero."));
            }

            if (row.IsExpiryTracked == true && row.IsLotTracked != true)
            {
                issues.Add(new ImportIssue(line, nameof(row.IsLotTracked),
                    "Expiry tracking requires lot tracking: an expiry date belongs to a batch."));
            }

            if (issues.Count > rowIssues)
            {
                continue;
            }

            if (existing.TryGetValue(row.Sku, out var current))
            {
                var changed = ApplyTo(current, row, abc, temperature, normalisedGtin);
                if (changed)
                {
                    updates++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                var item = new Item
                {
                    Sku = row.Sku,
                    Description = row.Description,
                    BaseUom = string.IsNullOrWhiteSpace(row.BaseUom) ? "EA" : row.BaseUom,
                    AbcClass = abc,
                    TemperatureClass = temperature,
                    IsLotTracked = row.IsLotTracked ?? false,
                    IsSerialTracked = row.IsSerialTracked ?? false,
                    IsExpiryTracked = row.IsExpiryTracked ?? false,
                    ShelfLifeDays = row.ShelfLifeDays,
                    IsHazardous = row.IsHazardous ?? false
                };

                item.PackConfigurations.Add(new PackConfiguration
                {
                    ItemId = item.Id,
                    Level = PackagingLevel.Each,
                    BaseUnitQuantity = 1,
                    IsPickUnit = true
                });

                if (row.CaseQuantity is { } caseQuantity)
                {
                    item.PackConfigurations.Add(new PackConfiguration
                    {
                        ItemId = item.Id,
                        Level = PackagingLevel.Case,
                        BaseUnitQuantity = caseQuantity,
                        Gtin = normalisedGtin,
                        GrossWeightKg = row.CaseWeightKg,
                        IsReceivingUnit = true
                    });
                }

                toCreate.Add(item);
            }
        }

        // All or nothing. A file with one bad row changes nothing at all, so the
        // person fixing it always knows the database matches the last good file.
        var committed = false;

        if (!dryRun && issues.Count == 0)
        {
            dbContext.Items.AddRange(toCreate);
            await dbContext.SaveChangesAsync(cancellationToken);
            committed = true;
        }
        else if (dryRun)
        {
            // Discard the tracked mutations made while diffing.
            dbContext.ChangeTracker.Clear();
        }

        return new ImportResult(
            DryRun: dryRun,
            Committed: committed,
            RowsRead: rows.Count,
            WouldCreate: toCreate.Count,
            WouldUpdate: updates,
            Unchanged: unchanged,
            Issues: issues);
    }

    private static bool ApplyTo(
        Item item,
        ItemImportRow row,
        AbcClass abc,
        TemperatureClass temperature,
        string? caseGtin)
    {
        var changed = false;

        void Set<T>(Func<T> read, Action<T> write, T value)
        {
            if (!EqualityComparer<T>.Default.Equals(read(), value))
            {
                write(value);
                changed = true;
            }
        }

        Set(() => item.Description, v => item.Description = v, row.Description);
        Set(() => item.AbcClass, v => item.AbcClass = v, abc);
        Set(() => item.TemperatureClass, v => item.TemperatureClass = v, temperature);

        if (row.IsLotTracked is { } lot) Set(() => item.IsLotTracked, v => item.IsLotTracked = v, lot);
        if (row.IsSerialTracked is { } serial) Set(() => item.IsSerialTracked, v => item.IsSerialTracked = v, serial);
        if (row.IsExpiryTracked is { } expiry) Set(() => item.IsExpiryTracked, v => item.IsExpiryTracked = v, expiry);
        if (row.ShelfLifeDays is { } shelf) Set(() => item.ShelfLifeDays, v => item.ShelfLifeDays = v, shelf);
        if (row.IsHazardous is { } hazard) Set(() => item.IsHazardous, v => item.IsHazardous = v, hazard);

        var casePack = item.PackConfigurations.FirstOrDefault(p => p.Level == PackagingLevel.Case);
        if (casePack is not null)
        {
            if (caseGtin is not null) Set(() => casePack.Gtin, v => casePack.Gtin = v, caseGtin);
            if (row.CaseQuantity is { } quantity)
            {
                Set(() => casePack.BaseUnitQuantity, v => casePack.BaseUnitQuantity = v, quantity);
            }
        }

        return changed;
    }
}
