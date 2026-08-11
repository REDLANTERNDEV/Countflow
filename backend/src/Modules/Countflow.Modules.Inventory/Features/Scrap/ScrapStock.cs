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

namespace Countflow.Modules.Inventory.Features.Scrap;

/// <param name="Notes">
/// Required, as an adjustment's reason is. A write-off is the one movement with no
/// document behind it: a receipt has a delivery note, a despatch has an order, and
/// a destruction has only the person who decided on it.
/// </param>
public sealed record ScrapStockRequest(
    string Sku,
    decimal Quantity,
    string LocationCode,
    ScrapCause Cause,
    string Notes,
    string? LotNumber = null,
    string? Sscc = null,
    InventoryStatus? Status = null);

/// <summary>
/// Writes stock off to the Scrap counterparty.
/// <para>
/// The movement type has had shape rules and no endpoint since Phase 2, and it
/// belongs to Phase 5 rather than any other because of what it is for: a write-off
/// is what a count variance turns into once somebody has gone and looked. The
/// count says the shelf holds eight rather than ten; the write-off is what happens
/// when the two missing cases are found in the aisle, crushed.
/// </para>
/// <para>
/// <b>Not the same movement as an adjustment, and the difference is the whole
/// point of having two counterparties.</b> An adjustment says the number was
/// wrong. A scrap says the number was right and the goods are now gone —
/// destroyed, and never to be sold. Posting both against one account would net a
/// real physical loss against a bookkeeping correction, and the shrinkage report
/// would show whichever was larger.
/// </para>
/// <para>
/// Supervisors and admins, as adjustments are. Destroying stock is not directed
/// work.
/// </para>
/// </summary>
public static class ScrapStock
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/scrap", async (
                ScrapStockRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                IStockPosting posting,
                StockReferenceResolver resolve,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, idempotencyKey, posting, resolve, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inventory")
            .WithName("ScrapStock")
            .WithSummary("Write stock off to the Scrap counterparty.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        ScrapStockRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        IStockPosting posting,
        StockReferenceResolver resolve,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<MovementReceiptResponse>(actor.Error);

        var item = await resolve.ItemAsync(request.Sku, cancellationToken);
        if (item.IsFailure) return Result.Failure<MovementReceiptResponse>(item.Error);

        var location = await resolve.LocationAsync(request.LocationCode, cancellationToken);
        if (location.IsFailure) return Result.Failure<MovementReceiptResponse>(location.Error);

        // A batch that already exists, as an adjustment requires. Destroying goods
        // of a batch the system has never heard of means the receipt was missed,
        // and posting the write-off would hide that.
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

        // Resolved for the response rather than for the posting, which derives it
        // from the origin itself. A confirmation that named the destination "SCRAP"
        // would be the one place in the system a location code was invented rather
        // than read.
        var scrapAccount = await resolve.CounterpartyAsync(
            location.Value.WarehouseId, CounterpartyKind.Scrap, cancellationToken);
        if (scrapAccount.IsFailure) return Result.Failure<MovementReceiptResponse>(scrapAccount.Error);

        var status = request.Status ?? InventoryStatus.Available;

        var posted = await posting.ScrapAsync(
            new ScrapInstruction
            {
                ItemId = item.Value.Id,
                LotId = lot.Value,
                Quantity = request.Quantity,
                FromLocationId = location.Value.Id,
                LicensePlateId = plate.Value,
                Condition = ToCondition(status),
                Cause = request.Cause,
                IdempotencyKey = idempotencyKey ?? string.Empty,
                Actor = actor.Value,
                Notes = request.Notes
            },
            cancellationToken);
        if (posted.IsFailure) return Result.Failure<MovementReceiptResponse>(posted.Error);

        return new MovementReceiptResponse(
            posted.Value.MovementId,
            posted.Value.Sequence,
            posted.Value.OccurredAt,
            item.Value.Sku,
            posted.Value.Quantity,
            location.Value.Code,
            scrapAccount.Value.Code,
            status,
            status,
            request.LotNumber,
            request.Sscc,
            posted.Value.FromQuantityAfter,
            posted.Value.ToQuantityAfter,
            posted.Value.WasAlreadyPosted);
    }

    private static StockCondition ToCondition(InventoryStatus status) => status switch
    {
        InventoryStatus.Quarantine => StockCondition.Quarantine,
        InventoryStatus.Damaged => StockCondition.Damaged,
        InventoryStatus.OnHold => StockCondition.OnHold,
        InventoryStatus.Expired => StockCondition.Expired,
        _ => StockCondition.Available
    };
}
