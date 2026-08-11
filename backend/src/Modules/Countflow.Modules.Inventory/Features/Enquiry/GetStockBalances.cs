using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Enquiry;

/// <param name="Quantity">On hand — what a stocktake would count.</param>
/// <param name="ReservedQuantity">
/// Committed to orders that have not been picked yet, and the distinction Phase 2
/// deliberately did not draw. Null when the figures are historical: reservations
/// are current state rather than an append-only history, so unlike a balance they
/// cannot honestly be reconstructed as of a past instant, and reporting today's
/// commitments against last week's stock would be worse than reporting none.
/// </param>
/// <param name="AvailableQuantity">
/// What can still be promised. Clamped at zero, because an ad-hoc transfer can
/// legitimately move stock out from under a claim and a negative availability is
/// not a thing anybody can act on.
/// </param>
public sealed record StockBalanceResponse(
    string Sku,
    string Description,
    string BaseUom,
    string? LotNumber,
    DateOnly? ExpiryDate,
    string LocationCode,
    string? Sscc,
    InventoryStatus Status,
    decimal Quantity,
    bool IsAllocatable,
    decimal? ReservedQuantity,
    decimal? AvailableQuantity);

/// <param name="AsOf">
/// Echoed back when the answer was reconstructed from the ledger rather than read
/// from the projection, so a client can label the figures as historical.
/// </param>
public sealed record StockEnquiryResponse(
    IReadOnlyList<StockBalanceResponse> Items,
    int Total,
    int Page,
    int PageSize,
    DateTimeOffset? AsOf,
    long? AsOfSequence);

/// <summary>
/// Stock enquiry: what is where, now or at any past instant.
/// <para>
/// The two modes are not two features. <c>asOf</c> absent reads the projection;
/// <c>asOf</c> present replays the ledger up to that point. Both are the same
/// question, and the second one is only answerable at all because the ledger is
/// immutable — "show me A-07-03-02 as of 14 July" is precisely the query a mutable
/// quantity column can never satisfy (ADR 0001).
/// </para>
/// </summary>
public static class GetStockBalances
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventory/balances", async (
                InventoryDbContext dbContext,
                LedgerReplay replay,
                ReservationLedger reservations,
                IItemDirectory items,
                ILocationDirectory locations,
                CancellationToken cancellationToken,
                string? sku = null,
                string? location = null,
                string? lot = null,
                string? sscc = null,
                InventoryStatus? status = null,
                bool includeEmpty = false,
                DateTimeOffset? asOf = null,
                int page = 1,
                int pageSize = 50) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 500);
                page = Math.Max(page, 1);

                var filters = await ResolveFiltersAsync(
                    dbContext, items, locations, sku, location, lot, sscc, cancellationToken);

                if (filters.IsFailure)
                {
                    return Result.Failure<StockEnquiryResponse>(filters.Error).ToHttpResult();
                }

                var holdings = asOf is { } instant
                    ? await ReplayAsync(replay, filters.Value, status, instant, cancellationToken)
                    : await ProjectionAsync(dbContext, filters.Value, status, cancellationToken);

                if (holdings.IsFailure)
                {
                    return Result.Failure<StockEnquiryResponse>(holdings.Error).ToHttpResult();
                }

                var rows = holdings.Value.Rows;

                if (!includeEmpty)
                {
                    // A zero row is not noise — it records that this bin once held
                    // this SKU, which putaway rules care about — but it is not what
                    // someone asking "what is in stock" means.
                    rows = [.. rows.Where(r => r.Quantity != 0m)];
                }

                var total = rows.Count;

                // Ordered by location id rather than location code, because the
                // codes live in another module and sorting by them would mean
                // resolving the whole result set before paging it. Location ids are
                // UUIDv7 and were created in layout order, so in practice this
                // reads aisle by aisle — but it is an ordering that happens to be
                // useful, not one the API promises.
                var window = rows
                    .OrderBy(r => r.LocationId)
                    .ThenBy(r => r.ItemId)
                    .ThenBy(r => r.Status)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                var response = await DescribeAsync(
                    dbContext, items, locations, reservations, window, cancellationToken);

                return Results.Ok(new StockEnquiryResponse(
                    response,
                    total,
                    page,
                    pageSize,
                    asOf,
                    holdings.Value.AsOfSequence));
            })
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("GetStockBalances")
            .WithSummary("Stock on hand by item, location, lot or licence plate — now or as of a past instant.")
            .Produces<StockEnquiryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A holding and its quantity, independent of where the number came from. Both
    /// modes reduce to this so the paging, filtering and description code below is
    /// written once.
    /// </summary>
    /// <param name="BalanceId">
    /// Null on a replayed row. A reconstructed holding is a sum over the ledger
    /// rather than a row that exists, so there is nothing for a commitment to hang
    /// off — which is exactly why the reserved column is blank in as-of mode.
    /// </param>
    private sealed record Row(
        Guid? BalanceId,
        Guid ItemId,
        Guid? LotId,
        Guid LocationId,
        Guid? LicensePlateId,
        InventoryStatus Status,
        decimal Quantity);

    private sealed record Holdings(List<Row> Rows, long? AsOfSequence);

    private sealed record Filters(Guid? ItemId, Guid? LocationId, Guid? LotId, Guid? LicensePlateId);

    private static async Task<Result<Filters>> ResolveFiltersAsync(
        InventoryDbContext dbContext,
        IItemDirectory items,
        ILocationDirectory locations,
        string? sku,
        string? locationCode,
        string? lotNumber,
        string? sscc,
        CancellationToken cancellationToken)
    {
        Guid? itemId = null;
        if (!string.IsNullOrWhiteSpace(sku))
        {
            var item = await items.FindBySkuAsync(sku.Trim(), cancellationToken);
            if (item is null) return Result.Failure<Filters>(InventoryErrors.ItemNotFound);
            itemId = item.Id;
        }

        Guid? locationId = null;
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            var found = await locations.FindByCodeAsync(locationCode.Trim(), cancellationToken);
            if (found is null) return Result.Failure<Filters>(InventoryErrors.LocationNotFound);
            locationId = found.Id;
        }

        Guid? lotId = null;
        if (!string.IsNullOrWhiteSpace(lotNumber))
        {
            var trimmed = lotNumber.Trim();

            // A lot number is unique per item, so without a SKU it can match
            // several. Filtering by all of them is the useful reading of the
            // question rather than an error — "where is batch 4711" is a
            // reasonable thing to ask.
            var candidates = await dbContext.Lots
                .AsNoTracking()
                .Where(l => l.LotNumber == trimmed && (itemId == null || l.ItemId == itemId))
                .Select(l => l.Id)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return Result.Failure<Filters>(InventoryErrors.LotNotFound);
            if (candidates.Count == 1) lotId = candidates[0];
            else return Result.Failure<Filters>(Error.Validation(
                "inventory.lot_ambiguous",
                $"Lot number '{trimmed}' exists on {candidates.Count} items. Narrow it with a SKU."));
        }

        Guid? plateId = null;
        if (!string.IsNullOrWhiteSpace(sscc))
        {
            var normalised = sscc.Trim();

            var found = await dbContext.LicensePlates
                .AsNoTracking()
                .Where(p => p.Sscc == normalised)
                .Select(p => (Guid?)p.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (found is null) return Result.Failure<Filters>(InventoryErrors.LicensePlateNotFound);
            plateId = found;
        }

        return new Filters(itemId, locationId, lotId, plateId);
    }

    private static async Task<Result<Holdings>> ProjectionAsync(
        InventoryDbContext dbContext,
        Filters filters,
        InventoryStatus? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.StockBalances.AsNoTracking();

        if (filters.ItemId is { } itemId) query = query.Where(b => b.ItemId == itemId);
        if (filters.LocationId is { } locationId) query = query.Where(b => b.LocationId == locationId);
        if (filters.LotId is { } lotId) query = query.Where(b => b.LotId == lotId);
        if (filters.LicensePlateId is { } plateId) query = query.Where(b => b.LicensePlateId == plateId);
        if (status is { } wanted) query = query.Where(b => b.Status == wanted);

        var rows = await query
            .Select(b => new Row(b.Id, b.ItemId, b.LotId, b.LocationId, b.LicensePlateId, b.Status, b.Quantity))
            .ToListAsync(cancellationToken);

        return new Holdings(rows, AsOfSequence: null);
    }

    private static async Task<Result<Holdings>> ReplayAsync(
        LedgerReplay replay,
        Filters filters,
        InventoryStatus? status,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        if (asOf > DateTimeOffset.UtcNow)
        {
            return Result.Failure<Holdings>(InventoryErrors.AsOfInFuture);
        }

        var sequence = await replay.ResolveSequenceAsync(asOf, cancellationToken);

        if (sequence is null)
        {
            // Nothing had been posted yet, so every balance was zero. An empty
            // result is the correct answer, not a missing one.
            return new Holdings([], AsOfSequence: null);
        }

        var replayed = await replay.ReplayAsync(
            sequence,
            new ReplayFilter(filters.ItemId, filters.LotId, filters.LocationId, filters.LicensePlateId),
            cancellationToken);

        var rows = replayed
            .Where(r => status is null || r.Status == status)
            .Select(r => new Row(null, r.ItemId, r.LotId, r.LocationId, r.LicensePlateId, r.Status, r.Quantity))
            .ToList();

        return new Holdings(rows, sequence);
    }

    /// <summary>
    /// Adds the human-readable identifiers.
    /// <para>
    /// Two batch lookups across the module boundary rather than a join — inventory
    /// cannot join to <c>masterdata.Items</c> (ADR 0002). This is the honest price
    /// of the boundary, and it is paid once per page rather than once per row.
    /// </para>
    /// </summary>
    private static async Task<List<StockBalanceResponse>> DescribeAsync(
        InventoryDbContext dbContext,
        IItemDirectory items,
        ILocationDirectory locations,
        ReservationLedger reservations,
        List<Row> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var reservedByBalance = await reservations.OpenByBalanceAsync(
            [.. rows.Where(r => r.BalanceId is not null).Select(r => r.BalanceId!.Value)],
            cancellationToken);

        var itemsById = await items.FindManyAsync(
            [.. rows.Select(r => r.ItemId).Distinct()], cancellationToken);

        var locationsById = await locations.FindManyAsync(
            [.. rows.Select(r => r.LocationId).Distinct()], cancellationToken);

        var lotIds = rows.Where(r => r.LotId is not null).Select(r => r.LotId!.Value).Distinct().ToList();

        var lotsById = lotIds.Count == 0
            ? []
            : await dbContext.Lots
                .AsNoTracking()
                .Where(l => lotIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, cancellationToken);

        var plateIds = rows.Where(r => r.LicensePlateId is not null)
            .Select(r => r.LicensePlateId!.Value).Distinct().ToList();

        var platesById = plateIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.LicensePlates
                .AsNoTracking()
                .Where(p => plateIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Sscc, cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                var item = itemsById.GetValueOrDefault(row.ItemId);
                var lot = row.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null;

                decimal? reserved = row.BalanceId is { } balanceId
                    ? reservedByBalance.GetValueOrDefault(balanceId)
                    : null;

                return new StockBalanceResponse(
                    item?.Sku ?? string.Empty,
                    item?.Description ?? string.Empty,
                    item?.BaseUom ?? string.Empty,
                    lot?.LotNumber,
                    lot?.ExpiryDate,
                    locationsById.GetValueOrDefault(row.LocationId)?.Code ?? string.Empty,
                    row.LicensePlateId is { } plateId ? platesById.GetValueOrDefault(plateId) : null,
                    row.Status,
                    row.Quantity,
                    row.Status.IsAllocatable(),
                    reserved,
                    reserved is { } committed ? Math.Max(0m, row.Quantity - committed) : null);
            })
        ];
    }
}
