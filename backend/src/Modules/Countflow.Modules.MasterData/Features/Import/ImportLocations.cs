using System.Globalization;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Results;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Import;

/// <summary>
/// Bulk location import — how a real warehouse's rack labels get loaded.
/// <para>
/// Same discipline as the item import: validate the entire file, commit all or
/// nothing, dry run by default. Location codes end up printed on physical
/// labels, so a half-applied import is expensive to unpick.
/// </para>
/// </summary>
public static class ImportLocations
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/import/locations", async (
                HttpRequest httpRequest,
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken,
                string warehouse = "WH1",
                bool dryRun = true) =>
            {
                using var reader = new StreamReader(httpRequest.Body);

                var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null,
                    MissingFieldFound = null,
                    TrimOptions = TrimOptions.Trim,
                    PrepareHeaderForMatch = args => args.Header.Replace(" ", string.Empty).ToLowerInvariant()
                };

                using var csv = new CsvReader(reader, configuration);

                List<LocationImportRow> rows;
                try
                {
                    rows = await csv.GetRecordsAsync<LocationImportRow>(cancellationToken)
                        .ToListAsync(cancellationToken);
                }
                catch (CsvHelperException exception)
                {
                    return Result.Failure<ImportResult>(Error.Validation(
                        "import.malformed_csv",
                        $"The file could not be read as CSV: {exception.Message}")).ToHttpResult();
                }

                var target = await dbContext.Warehouses
                    .SingleOrDefaultAsync(w => w.Code == warehouse, cancellationToken);

                if (target is null)
                {
                    return Result.Failure<ImportResult>(Error.NotFound(
                        "warehouse.not_found",
                        $"No warehouse with code '{warehouse}'.")).ToHttpResult();
                }

                var result = await ProcessAsync(rows, target, dbContext, dryRun, cancellationToken);
                return Results.Ok(result);
            })
            .RequireAuthorization()
            .Accepts<string>("text/csv")
            .WithTags("Import")
            .WithName("ImportLocations")
            .WithSummary("Import locations from CSV. Validates the whole file first; dry run by default.")
            .Produces<ImportResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Case-insensitive comparer for the (area, zone) lookup key.</summary>
    private static readonly IEqualityComparer<(string Area, string Zone)> TupleComparer =
        EqualityComparer<(string Area, string Zone)>.Create(
            (left, right) =>
                string.Equals(left.Area, right.Area, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Zone, right.Zone, StringComparison.OrdinalIgnoreCase),
            key => HashCode.Combine(
                key.Area.ToUpperInvariant(),
                key.Zone.ToUpperInvariant()));

    private static async Task<ImportResult> ProcessAsync(
        List<LocationImportRow> rows,
        Warehouse warehouse,
        MasterDataDbContext dbContext,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var issues = new List<ImportIssue>();

        var codes = rows.Select(r => r.Code).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        var existing = await dbContext.Locations
            .Where(l => codes.Contains(l.Code))
            .ToDictionaryAsync(l => l.Code, cancellationToken);

        // Keyed on area + zone. Zone codes are unique only within their area, so
        // keying on the zone code alone collides the moment a warehouse has a
        // "DOCK" zone in both receiving and shipping — which is every warehouse.
        var zones = (await dbContext.Zones
                .Where(z => z.Area!.WarehouseId == warehouse.Id)
                .Select(z => new { z.Id, z.Code, AreaCode = z.Area!.Code })
                .ToListAsync(cancellationToken))
            .ToDictionary(
                z => (z.AreaCode, z.Code),
                z => z.Id,
                TupleComparer);

        foreach (var group in rows
                     .Select((row, index) => (row, line: index + 2))
                     .GroupBy(x => x.row.Code, StringComparer.OrdinalIgnoreCase)
                     .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
        {
            foreach (var (_, line) in group.Skip(1))
            {
                issues.Add(new ImportIssue(line, nameof(LocationImportRow.Code),
                    $"Location code '{group.Key}' appears more than once in this file."));
            }
        }

        var toCreate = new List<Location>();
        var updates = 0;
        var unchanged = 0;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var line = index + 2;
            var rowIssues = issues.Count;

            if (string.IsNullOrWhiteSpace(row.Code))
            {
                issues.Add(new ImportIssue(line, nameof(row.Code), "Code is required."));
            }

            if (!Enum.TryParse<LocationType>(row.Type, true, out var type))
            {
                issues.Add(new ImportIssue(line, nameof(row.Type),
                    $"'{row.Type}' is not a valid location type."));
            }

            Guid? zoneId = null;

            if (!type.IsVirtual())
            {
                if (string.IsNullOrWhiteSpace(row.Area) || string.IsNullOrWhiteSpace(row.Zone))
                {
                    issues.Add(new ImportIssue(line, nameof(row.Zone),
                        "Area and Zone are both required for a physical location."));
                }
                else if (zones.TryGetValue((row.Area, row.Zone), out var found))
                {
                    zoneId = found;

                    // The code carries the same area and zone in segments 2 and 3.
                    // Cross-checking them catches the spreadsheet copy-paste where
                    // a row's code and its columns disagree — which would otherwise
                    // produce a correctly-stored location with a wrong label.
                    var segments = row.Code.Split(LocationCode.Separator);
                    if (segments.Length >= 3
                        && (!segments[1].Equals(row.Area, StringComparison.OrdinalIgnoreCase)
                            || !segments[2].Equals(row.Zone, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new ImportIssue(line, nameof(row.Code),
                            $"Code '{row.Code}' does not match Area '{row.Area}' and Zone '{row.Zone}'."));
                    }
                }
                else
                {
                    issues.Add(new ImportIssue(line, nameof(row.Zone),
                        $"Zone '{row.Area}/{row.Zone}' does not exist in warehouse {warehouse.Code}."));
                }
            }

            if (row.MaxWeightKg is < 0)
            {
                issues.Add(new ImportIssue(line, nameof(row.MaxWeightKg), "Max weight cannot be negative."));
            }

            if (row.MaxVolumeM3 is < 0)
            {
                issues.Add(new ImportIssue(line, nameof(row.MaxVolumeM3), "Max volume cannot be negative."));
            }

            // A location nobody can pick from but that is flagged replenishable
            // would have replenishment topping up a bin no picker ever visits.
            if (row.IsReplenishable == true && row.IsPickable == false)
            {
                issues.Add(new ImportIssue(line, nameof(row.IsReplenishable),
                    "A replenishable location must also be pickable; replenishment exists to feed a pick face."));
            }

            if (issues.Count > rowIssues)
            {
                continue;
            }

            if (existing.TryGetValue(row.Code, out var current))
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

                Set(() => current.Type, v => current.Type = v, type);
                if (row.PickSequence is { } sequence) Set(() => current.PickSequence, v => current.PickSequence = v, sequence);
                if (row.MaxVolumeM3 is { } volume) Set(() => current.MaxVolumeM3, v => current.MaxVolumeM3 = v, volume);
                if (row.MaxWeightKg is { } weight) Set(() => current.MaxWeightKg, v => current.MaxWeightKg = v, weight);
                if (row.MaxDistinctItems is { } distinct) Set(() => current.MaxDistinctItems, v => current.MaxDistinctItems = v, distinct);
                if (row.IsPickable is { } pickable) Set(() => current.IsPickable, v => current.IsPickable = v, pickable);
                if (row.IsReplenishable is { } replen) Set(() => current.IsReplenishable, v => current.IsReplenishable = v, replen);
                if (row.IsActive is { } active) Set(() => current.IsActive, v => current.IsActive = v, active);

                if (changed) updates++; else unchanged++;
            }
            else
            {
                toCreate.Add(new Location
                {
                    WarehouseId = warehouse.Id,
                    ZoneId = zoneId,
                    Code = row.Code,
                    Type = type,
                    Aisle = row.Aisle,
                    Bay = row.Bay,
                    Level = row.Level,
                    Position = row.Position,
                    PickSequence = row.PickSequence ?? 0,
                    MaxVolumeM3 = row.MaxVolumeM3,
                    MaxWeightKg = row.MaxWeightKg,
                    MaxDistinctItems = row.MaxDistinctItems,
                    IsPickable = row.IsPickable ?? false,
                    IsReplenishable = row.IsReplenishable ?? false,
                    IsActive = row.IsActive ?? true
                });
            }
        }

        var committed = false;

        if (!dryRun && issues.Count == 0)
        {
            dbContext.Locations.AddRange(toCreate);
            await dbContext.SaveChangesAsync(cancellationToken);
            committed = true;
        }
        else if (dryRun)
        {
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
}
