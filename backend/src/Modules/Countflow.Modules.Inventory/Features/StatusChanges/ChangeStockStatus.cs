using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Inventory.Features.StatusChanges;

public sealed record ChangeStockStatusRequest(
    string Sku,
    decimal Quantity,
    string LocationCode,
    InventoryStatus FromStatus,
    InventoryStatus ToStatus,
    MovementReason Reason,
    string? LotNumber = null,
    string? Sscc = null,
    string? Notes = null);

/// <summary>
/// Quarantines, releases or writes down stock in place: the same location and
/// quantity, a different disposition.
/// <para>
/// A movement rather than a column update, which is the whole point (ADR 0001).
/// "Who quarantined this pallet, when, and why" is exactly the question asked
/// after a recall, and a status flag that was overwritten in place cannot answer
/// it. Because it goes through the same ledger path as a physical move, the answer
/// is in the same audit trail and needs no separate history table.
/// </para>
/// </summary>
public static class ChangeStockStatus
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/status-changes", async (
                ChangeStockStatusRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                StockLedger ledger,
                StockReferenceResolver resolve,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, idempotencyKey, ledger, resolve, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("ChangeStockStatus")
            .WithSummary("Move stock to a different inventory status in place.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        ChangeStockStatusRequest request,
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

        var location = await resolve.LocationAsync(request.LocationCode, cancellationToken);
        if (location.IsFailure) return Result.Failure<MovementReceiptResponse>(location.Error);

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

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = item.Value.Id,
                LotId = lot.Value,
                Quantity = request.Quantity,
                From = new Holding(location.Value.Id, plate.Value, request.FromStatus),
                To = new Holding(location.Value.Id, plate.Value, request.ToStatus),
                Type = MovementType.StatusChange,
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
            location.Value.Code,
            location.Value.Code,
            request.FromStatus,
            request.ToStatus,
            request.LotNumber,
            request.Sscc);
    }
}
