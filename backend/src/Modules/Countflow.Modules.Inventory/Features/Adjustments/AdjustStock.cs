using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Inventory.Features.Adjustments;

/// <param name="Quantity">
/// Signed, and the only place in the module where that is true. Positive is a
/// gain, negative a loss. The ledger itself only ever stores positive quantities —
/// this sign chooses which side of the entry the Adjustment counterparty goes on.
/// </param>
/// <param name="Reason">
/// Required, unlike every other command. An adjustment with no stated cause is the
/// single most common way a WMS loses the ability to explain its own numbers.
/// </param>
public sealed record AdjustStockRequest(
    string Sku,
    decimal Quantity,
    string LocationCode,
    MovementReason Reason,
    string? LotNumber = null,
    string? Sscc = null,
    InventoryStatus? Status = null,
    string? Notes = null);

/// <summary>
/// Corrects a quantity against the Adjustment counterparty — a count variance, a
/// found pallet, a loss nobody can account for.
/// <para>
/// Restricted to supervisors and admins. Operators execute directed work; deciding
/// that the system's figure is wrong is a different authority, and an adjustment
/// endpoint open to every handheld is how shrinkage gets papered over instead of
/// investigated.
/// </para>
/// </summary>
public static class AdjustStock
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/inventory/adjustments", async (
                AdjustStockRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                StockLedger ledger,
                StockReferenceResolver resolve,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, idempotencyKey, ledger, resolve, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inventory")
            .WithName("AdjustStock")
            .WithSummary("Adjust a quantity up or down against the Adjustment counterparty.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        AdjustStockRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        StockLedger ledger,
        StockReferenceResolver resolve,
        CancellationToken cancellationToken)
    {
        var actor = LedgerActor.From(user);
        if (actor.IsFailure) return Result.Failure<MovementReceiptResponse>(actor.Error);

        if (request.Quantity == 0)
        {
            return Result.Failure<MovementReceiptResponse>(InventoryErrors.QuantityNotPositive);
        }

        var item = await resolve.ItemAsync(request.Sku, cancellationToken);
        if (item.IsFailure) return Result.Failure<MovementReceiptResponse>(item.Error);

        var location = await resolve.LocationAsync(request.LocationCode, cancellationToken);
        if (location.IsFailure) return Result.Failure<MovementReceiptResponse>(location.Error);

        var counterparty = await resolve.CounterpartyAsync(
            location.Value.WarehouseId, CounterpartyKind.Adjustment, cancellationToken);
        if (counterparty.IsFailure) return Result.Failure<MovementReceiptResponse>(counterparty.Error);

        // An adjustment references a batch that already exists. Discovering stock
        // of an unknown batch is a receipt, not a correction — it came from
        // somewhere, and the ledger should say so.
        var lot = await resolve.LotAsync(
            item.Value,
            request.LotNumber,
            expiryDate: null,
            productionDate: null,
            createIfMissing: false,
            cancellationToken);
        if (lot.IsFailure) return Result.Failure<MovementReceiptResponse>(lot.Error);

        var plate = await resolve.LicensePlateAsync(request.Sscc, createIfMissing: false, cancellationToken);
        if (plate.IsFailure) return Result.Failure<MovementReceiptResponse>(plate.Error);

        var status = request.Status ?? InventoryStatus.Available;
        var holding = new Holding(location.Value.Id, plate.Value, status);
        var contra = new Holding(counterparty.Value.Id, null, status);

        var isGain = request.Quantity > 0;

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = item.Value.Id,
                LotId = lot.Value,
                Quantity = Math.Abs(request.Quantity),
                From = isGain ? contra : holding,
                To = isGain ? holding : contra,
                Type = request.Reason is MovementReason.CycleCountVariance or MovementReason.StocktakeVariance
                    ? MovementType.CountAdjustment
                    : MovementType.Adjustment,
                Reason = request.Reason,
                IdempotencyKey = idempotencyKey ?? string.Empty,
                Actor = actor.Value,
                Notes = request.Notes
            },
            cancellationToken);
        if (posted.IsFailure) return Result.Failure<MovementReceiptResponse>(posted.Error);

        return MovementReceiptResponse.From(
            posted.Value,
            item.Value.Sku,
            isGain ? counterparty.Value.Code : location.Value.Code,
            isGain ? location.Value.Code : counterparty.Value.Code,
            status,
            status,
            request.LotNumber,
            request.Sscc);
    }
}
