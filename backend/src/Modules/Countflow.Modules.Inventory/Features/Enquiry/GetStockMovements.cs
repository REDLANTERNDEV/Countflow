using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Enquiry;

public sealed record StockMovementResponse(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Sku,
    string? LotNumber,
    decimal Quantity,
    string FromLocationCode,
    string ToLocationCode,
    string? FromSscc,
    string? ToSscc,
    InventoryStatus FromStatus,
    InventoryStatus ToStatus,
    MovementType Type,
    MovementReason Reason,
    string UserName,
    Guid? ReversesMovementId,
    bool IsReversed,
    Guid? TaskId,
    Guid? DecisionTraceId,
    string? Notes);

/// <summary>
/// The ledger itself, read back.
/// <para>
/// This is the stock card an auditor, a supplier claim and a shrinkage
/// investigation all ask for, and it needs no separate history table because the
/// ledger has always been the history. Ordered newest first by sequence rather than
/// by timestamp — during a wave several movements land in the same millisecond, and
/// a list that reorders itself between requests is not evidence of anything.
/// </para>
/// </summary>
public static class GetStockMovements
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventory/movements", async (
                InventoryDbContext dbContext,
                IItemDirectory items,
                ILocationDirectory locations,
                CancellationToken cancellationToken,
                string? sku = null,
                string? location = null,
                string? lot = null,
                string? sscc = null,
                MovementType? type = null,
                MovementReason? reason = null,
                DateTimeOffset? from = null,
                DateTimeOffset? to = null,
                int page = 1,
                int pageSize = 50) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 500);
                page = Math.Max(page, 1);

                var query = dbContext.StockMovements.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(sku))
                {
                    var item = await items.FindBySkuAsync(sku.Trim(), cancellationToken);
                    if (item is null)
                    {
                        return Result.Failure<InventoryPage<StockMovementResponse>>(
                            InventoryErrors.ItemNotFound).ToHttpResult();
                    }

                    query = query.Where(m => m.ItemId == item.Id);
                }

                if (!string.IsNullOrWhiteSpace(location))
                {
                    var found = await locations.FindByCodeAsync(location.Trim(), cancellationToken);
                    if (found is null)
                    {
                        return Result.Failure<InventoryPage<StockMovementResponse>>(
                            InventoryErrors.LocationNotFound).ToHttpResult();
                    }

                    // Both sides: "what happened in this bin" includes departures.
                    query = query.Where(m => m.FromLocationId == found.Id || m.ToLocationId == found.Id);
                }

                if (!string.IsNullOrWhiteSpace(lot))
                {
                    var trimmed = lot.Trim();
                    var lotIds = await dbContext.Lots
                        .AsNoTracking()
                        .Where(l => l.LotNumber == trimmed)
                        .Select(l => l.Id)
                        .ToListAsync(cancellationToken);

                    query = query.Where(m => m.LotId != null && lotIds.Contains(m.LotId.Value));
                }

                if (!string.IsNullOrWhiteSpace(sscc))
                {
                    var trimmed = sscc.Trim();
                    var plateId = await dbContext.LicensePlates
                        .AsNoTracking()
                        .Where(p => p.Sscc == trimmed)
                        .Select(p => (Guid?)p.Id)
                        .SingleOrDefaultAsync(cancellationToken);

                    if (plateId is null)
                    {
                        return Result.Failure<InventoryPage<StockMovementResponse>>(
                            InventoryErrors.LicensePlateNotFound).ToHttpResult();
                    }

                    query = query.Where(m =>
                        m.FromLicensePlateId == plateId || m.ToLicensePlateId == plateId);
                }

                if (type is { } wantedType) query = query.Where(m => m.Type == wantedType);
                if (reason is { } wantedReason) query = query.Where(m => m.Reason == wantedReason);
                if (from is { } after) query = query.Where(m => m.OccurredAt >= after);
                if (to is { } before) query = query.Where(m => m.OccurredAt <= before);

                var total = await query.CountAsync(cancellationToken);

                var movements = await query
                    .OrderByDescending(m => m.Sequence)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);

                var rows = await DescribeAsync(dbContext, items, locations, movements, cancellationToken);

                return Results.Ok(new InventoryPage<StockMovementResponse>(rows, total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("GetStockMovements")
            .WithSummary("Read the stock ledger, filtered and paged.")
            .Produces<InventoryPage<StockMovementResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<List<StockMovementResponse>> DescribeAsync(
        InventoryDbContext dbContext,
        IItemDirectory items,
        ILocationDirectory locations,
        List<StockMovement> movements,
        CancellationToken cancellationToken)
    {
        if (movements.Count == 0)
        {
            return [];
        }

        // Batched across the module boundary, once per page. See the same note on
        // GetStockBalances.
        var itemsById = await items.FindManyAsync(
            [.. movements.Select(m => m.ItemId).Distinct()], cancellationToken);

        var locationsById = await locations.FindManyAsync(
            [.. movements.SelectMany(m => new[] { m.FromLocationId, m.ToLocationId }).Distinct()],
            cancellationToken);

        var lotIds = movements.Where(m => m.LotId is not null).Select(m => m.LotId!.Value).Distinct().ToList();

        var lotsById = lotIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Lots
                .AsNoTracking()
                .Where(l => lotIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);

        var plateIds = movements
            .SelectMany(m => new[] { m.FromLicensePlateId, m.ToLicensePlateId })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var platesById = plateIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.LicensePlates
                .AsNoTracking()
                .Where(p => plateIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Sscc, cancellationToken);

        // Which of these entries has since been corrected. Shown in the list
        // because a reversed movement that looks identical to a live one is how a
        // reader draws the wrong conclusion from a correct ledger.
        var movementIds = movements.Select(m => m.Id).ToList();

        var reversed = await dbContext.StockMovements
            .AsNoTracking()
            .Where(m => m.ReversesMovementId != null && movementIds.Contains(m.ReversesMovementId.Value))
            .Select(m => m.ReversesMovementId!.Value)
            .ToListAsync(cancellationToken);

        var reversedSet = reversed.ToHashSet();

        return
        [
            .. movements.Select(m => new StockMovementResponse(
                m.Id,
                m.Sequence,
                m.OccurredAt,
                itemsById.GetValueOrDefault(m.ItemId)?.Sku ?? string.Empty,
                m.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null,
                m.Quantity,
                locationsById.GetValueOrDefault(m.FromLocationId)?.Code ?? string.Empty,
                locationsById.GetValueOrDefault(m.ToLocationId)?.Code ?? string.Empty,
                m.FromLicensePlateId is { } fromPlate ? platesById.GetValueOrDefault(fromPlate) : null,
                m.ToLicensePlateId is { } toPlate ? platesById.GetValueOrDefault(toPlate) : null,
                m.FromStatus,
                m.ToStatus,
                m.Type,
                m.Reason,
                m.UserName,
                m.ReversesMovementId,
                reversedSet.Contains(m.Id),
                // Both null before Phase 3 and both populated on directed work
                // afterwards. Exposed on the ledger read rather than only on the
                // task, because the ledger is what an auditor is given: a movement
                // that cannot say what directed it, or why, is exactly the
                // unanswerable entry ADR 0004 exists to prevent.
                m.TaskId,
                m.DecisionTraceId,
                m.Notes))
        ];
    }
}
