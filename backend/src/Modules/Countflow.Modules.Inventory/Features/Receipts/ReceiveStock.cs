using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Inventory.Features.Receipts;

/// <param name="Quantity">
/// In the item's base unit of measure. A caller holding a case barcode converts
/// first through <c>/api/scan/resolve</c>, which returns the base-unit figure —
/// nothing downstream of the scan seam reasons about packaging levels.
/// </param>
/// <param name="LotNumber">GS1 AI 10. Created on first sight; required if the item is lot-tracked.</param>
/// <param name="Sscc">The pallet's licence plate, if the goods arrived on one. Created on first sight.</param>
/// <param name="Status">
/// Defaults to Available. Set Quarantine to book suspect goods straight into hold
/// rather than receiving them and quarantining them afterwards — the two are not
/// equivalent, because only the first never makes the stock allocatable at all.
/// </param>
public sealed record ReceiveStockRequest(
    string Sku,
    decimal Quantity,
    string ToLocationCode,
    string? LotNumber = null,
    DateOnly? ExpiryDate = null,
    DateOnly? ProductionDate = null,
    string? Sscc = null,
    InventoryStatus? Status = null,
    MovementReason? Reason = null,
    string? Notes = null);

/// <summary>
/// Books goods into the building: a movement from the Supplier counterparty into a
/// physical location.
/// <para>
/// This is the primitive, not the process. Phase 3's inbound loop — ASN matching,
/// dock scanning, rule-driven putaway — will orchestrate this rather than replace
/// it, which is why it carries no purchase-order reference yet.
/// </para>
/// </summary>
public static class ReceiveStock
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/receipts", async (
                ReceiveStockRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                StockLedger ledger,
                StockReferenceResolver resolve,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, idempotencyKey, ledger, resolve, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("ReceiveStock")
            .WithSummary("Book goods in from the Supplier counterparty.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        ReceiveStockRequest request,
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

        var destination = await resolve.LocationAsync(request.ToLocationCode, cancellationToken);
        if (destination.IsFailure) return Result.Failure<MovementReceiptResponse>(destination.Error);

        // The supplier account belongs to the warehouse the goods are landing in,
        // so it is derived from the destination rather than asked for. One less
        // thing a caller can get wrong, and one less field in every EDI mapping.
        var supplier = await resolve.CounterpartyAsync(
            destination.Value.WarehouseId, CounterpartyKind.Supplier, cancellationToken);
        if (supplier.IsFailure) return Result.Failure<MovementReceiptResponse>(supplier.Error);

        var lot = await resolve.LotAsync(
            item.Value,
            request.LotNumber,
            request.ExpiryDate,
            request.ProductionDate,
            createIfMissing: true,
            cancellationToken);
        if (lot.IsFailure) return Result.Failure<MovementReceiptResponse>(lot.Error);

        var plate = await resolve.LicensePlateAsync(request.Sscc, createIfMissing: true, cancellationToken);
        if (plate.IsFailure) return Result.Failure<MovementReceiptResponse>(plate.Error);

        var status = request.Status ?? InventoryStatus.Available;

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = item.Value.Id,
                LotId = lot.Value,
                Quantity = request.Quantity,
                // Stock always leaves the supplier account as Available: from the
                // supplier's side it was good stock, and booking it into
                // quarantine is a decision this warehouse made. Both facts belong
                // in the same entry rather than in two.
                From = new Holding(supplier.Value.Id, null, InventoryStatus.Available),
                To = new Holding(destination.Value.Id, plate.Value, status),
                Type = MovementType.Receipt,
                Reason = request.Reason ?? MovementReason.PurchaseOrderReceipt,
                IdempotencyKey = idempotencyKey ?? string.Empty,
                Actor = actor.Value,
                Notes = request.Notes
            },
            cancellationToken);
        if (posted.IsFailure) return Result.Failure<MovementReceiptResponse>(posted.Error);

        return MovementReceiptResponse.From(
            posted.Value,
            item.Value.Sku,
            supplier.Value.Code,
            destination.Value.Code,
            InventoryStatus.Available,
            status,
            request.LotNumber,
            request.Sscc);
    }
}
