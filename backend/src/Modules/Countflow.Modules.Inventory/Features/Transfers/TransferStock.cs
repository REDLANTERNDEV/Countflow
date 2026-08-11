using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Inventory.Features.Transfers;

/// <param name="LotNumber">Must already exist. A transfer cannot invent a batch; only a receipt can.</param>
/// <param name="FromSscc">Which licence plate the stock is coming off. Omit for loose stock on the shelf.</param>
/// <param name="ToSscc">Which licence plate it is going onto. Created on first sight, so a picker can build a new tote.</param>
/// <param name="Type">
/// Defaults to Transfer. Putaway, Replenishment and Pick are the same movement
/// with a name the reports care about — separating them here is what lets travel
/// analysis distinguish a replen pull from a picker doubling back.
/// </param>
public sealed record TransferStockRequest(
    string Sku,
    decimal Quantity,
    string FromLocationCode,
    string ToLocationCode,
    string? LotNumber = null,
    string? FromSscc = null,
    string? ToSscc = null,
    InventoryStatus? Status = null,
    MovementType? Type = null,
    MovementReason? Reason = null,
    string? Notes = null);

/// <summary>
/// Moves stock from one holding to another inside the building.
/// <para>
/// One item and lot per call. A pallet carrying three SKUs is three calls, and
/// that is a deliberate Phase 2 boundary: a composite "move this whole licence
/// plate" command has to post several entries under one idempotency key, and the
/// place that needs it is Phase 3's putaway task. Keeping one command to one entry
/// keeps the idempotency guarantee trivially airtight while the ledger is new.
/// </para>
/// </summary>
public static class TransferStock
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/transfers", async (
                TransferStockRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                StockLedger ledger,
                StockReferenceResolver resolve,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, idempotencyKey, ledger, resolve, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("TransferStock")
            .WithSummary("Move stock between two locations, licence plates, or both.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        TransferStockRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        StockLedger ledger,
        StockReferenceResolver resolve,
        CancellationToken cancellationToken)
    {
        var actor = LedgerActor.From(user);
        if (actor.IsFailure) return Result.Failure<MovementReceiptResponse>(actor.Error);

        var item = await resolve.ItemAsync(request.Sku, cancellationToken);
        if (item.IsFailure) return Result.Failure<MovementReceiptResponse>(item.Error);

        var from = await resolve.LocationAsync(request.FromLocationCode, cancellationToken);
        if (from.IsFailure) return Result.Failure<MovementReceiptResponse>(from.Error);

        var to = await resolve.LocationAsync(request.ToLocationCode, cancellationToken);
        if (to.IsFailure) return Result.Failure<MovementReceiptResponse>(to.Error);

        var lot = await resolve.LotAsync(
            item.Value,
            request.LotNumber,
            expiryDate: null,
            productionDate: null,
            createIfMissing: false,
            cancellationToken);
        if (lot.IsFailure) return Result.Failure<MovementReceiptResponse>(lot.Error);

        // The source plate must exist — you cannot pick off a pallet the warehouse
        // has never seen. The destination may be new, because building a fresh
        // tote is exactly what a picker does.
        var fromPlate = await resolve.LicensePlateAsync(request.FromSscc, createIfMissing: false, cancellationToken);
        if (fromPlate.IsFailure) return Result.Failure<MovementReceiptResponse>(fromPlate.Error);

        var toPlate = await resolve.LicensePlateAsync(request.ToSscc, createIfMissing: true, cancellationToken);
        if (toPlate.IsFailure) return Result.Failure<MovementReceiptResponse>(toPlate.Error);

        // Status is preserved across a transfer. Moving quarantined stock must not
        // quietly release it, so changing disposition is its own command.
        var status = request.Status ?? InventoryStatus.Available;

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = item.Value.Id,
                LotId = lot.Value,
                Quantity = request.Quantity,
                From = new Holding(from.Value.Id, fromPlate.Value, status),
                To = new Holding(to.Value.Id, toPlate.Value, status),
                Type = request.Type ?? MovementType.Transfer,
                Reason = request.Reason ?? MovementReason.ConsolidationMove,
                IdempotencyKey = idempotencyKey ?? string.Empty,
                Actor = actor.Value,
                Notes = request.Notes
            },
            cancellationToken);
        if (posted.IsFailure) return Result.Failure<MovementReceiptResponse>(posted.Error);

        return MovementReceiptResponse.From(
            posted.Value,
            item.Value.Sku,
            from.Value.Code,
            to.Value.Code,
            status,
            status,
            request.LotNumber,
            request.ToSscc ?? request.FromSscc);
    }
}
