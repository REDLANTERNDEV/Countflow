using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Features.Scanning;

public sealed record ResolveScanRequest(string Barcode);

public sealed record ScanElementResponse(string Ai, string Title, string Value);

public sealed record ScannedItemResponse(
    Guid ItemId,
    string Sku,
    string Description,
    string PackagingLevel,
    int BaseUnitQuantity,
    bool IsLotTracked,
    bool IsExpiryTracked,
    bool IsSerialTracked);

public sealed record ScanResolutionResponse(
    bool ItemRecognised,
    ScannedItemResponse? Item,
    string? Gtin,
    string? Sscc,
    bool SsccCheckDigitValid,
    string? LotNumber,
    DateOnly? ExpiryDate,
    DateOnly? ProductionDate,
    string? SerialNumber,
    int? ScannedQuantity,
    int? BaseUnitQuantity,
    IReadOnlyList<ScanElementResponse> Elements,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a raw scan into everything the warehouse knows about it.
/// <para>
/// This is the seam the whole scanning story hangs off: one scan of a GS1-128
/// logistics label yields item, packaging level, batch, expiry and quantity in
/// one round trip. Phase 3's receiving flow calls exactly this before writing
/// anything to the ledger.
/// </para>
/// </summary>
public static class ResolveScan
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/scan/resolve", async (
                ResolveScanRequest request,
                MasterDataDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var parsed = Gs1Parser.Parse(request.Barcode);
                if (parsed.IsFailure)
                {
                    return Result.Failure<ScanResolutionResponse>(parsed.Error).ToHttpResult();
                }

                var scan = parsed.Value;
                var warnings = new List<string>();

                var elements = scan.Elements
                    .Select(e => new ScanElementResponse(e.Ai, e.Title, e.Value))
                    .ToList();

                ScannedItemResponse? item = null;
                int? baseUnits = null;

                if (scan.Gtin is { } gtin)
                {
                    // The GTIN identifies a packaging level, not an item, so this
                    // resolves the pack row and reads the item through it. That is
                    // how one scan can tell a case of 24 from a single unit.
                    var pack = await dbContext.PackConfigurations
                        .AsNoTracking()
                        .Include(p => p.Item)
                        .SingleOrDefaultAsync(p => p.Gtin == gtin.Value, cancellationToken);

                    if (pack?.Item is { } matched)
                    {
                        item = new ScannedItemResponse(
                            matched.Id,
                            matched.Sku,
                            matched.Description,
                            pack.Level.ToString(),
                            pack.BaseUnitQuantity,
                            matched.IsLotTracked,
                            matched.IsExpiryTracked,
                            matched.IsSerialTracked);

                        // Everything reaching the ledger is in base units, so the
                        // conversion happens here, once, at the edge.
                        baseUnits = (scan.Quantity ?? 1) * pack.BaseUnitQuantity;

                        if (matched.IsLotTracked && string.IsNullOrWhiteSpace(scan.LotNumber))
                        {
                            warnings.Add($"{matched.Sku} is lot-tracked but the scan carries no batch number (AI 10).");
                        }

                        if (matched.IsExpiryTracked && scan.ExpiryDate is null)
                        {
                            warnings.Add($"{matched.Sku} is expiry-tracked but the scan carries no expiry date (AI 17).");
                        }

                        if (matched.IsSerialTracked && string.IsNullOrWhiteSpace(scan.SerialNumber))
                        {
                            warnings.Add($"{matched.Sku} is serial-tracked but the scan carries no serial (AI 21).");
                        }

                        if (matched.IsExpiryTracked && scan.ExpiryDate is { } expiry
                            && expiry < DateOnly.FromDateTime(DateTime.UtcNow))
                        {
                            warnings.Add($"Stock is already expired ({expiry:yyyy-MM-dd}). It must be quarantined, not put away.");
                        }
                    }
                    else
                    {
                        warnings.Add($"GTIN {gtin.Value} is not in the item catalogue.");
                    }
                }
                else if (scan.Find("01") is { } malformed)
                {
                    warnings.Add($"GTIN '{malformed}' failed its check digit and was not used for lookup.");
                }

                if (scan.Sscc is not null && !scan.HasValidSscc)
                {
                    warnings.Add($"SSCC {scan.Sscc} failed its check digit.");
                }

                var response = new ScanResolutionResponse(
                    ItemRecognised: item is not null,
                    Item: item,
                    Gtin: scan.Gtin?.Value,
                    Sscc: scan.Sscc,
                    SsccCheckDigitValid: scan.Sscc is not null && scan.HasValidSscc,
                    LotNumber: scan.LotNumber,
                    ExpiryDate: scan.ExpiryDate,
                    ProductionDate: scan.ProductionDate,
                    SerialNumber: scan.SerialNumber,
                    ScannedQuantity: scan.Quantity,
                    BaseUnitQuantity: baseUnits,
                    Elements: elements,
                    Warnings: warnings);

                return Results.Ok(response);
            })
            .RequireAuthorization()
            .WithTags("Scanning")
            .WithName("ResolveScan")
            .WithSummary("Decode a GS1 barcode and resolve it against the item catalogue.")
            .Produces<ScanResolutionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
