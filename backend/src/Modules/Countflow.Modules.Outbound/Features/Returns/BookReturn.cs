using System.Security.Claims;
using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <summary>
/// Books goods a customer sent back, and puts an inspection in somebody's queue.
/// <para>
/// The first half of the return, and deliberately only the first half. Nothing here
/// decides what becomes of the goods: they land in a hold-role returns bay at
/// <c>OnHold</c>, which is a condition allocation will not sell out of, and the
/// disposition waits for a person to open the carton. That split is the whole
/// argument for a return being two events — every input the disposition rules
/// actually score on (sealed or opened, repairable or not, what it really is as
/// opposed to what the paperwork says) is a judgement somebody makes with the goods
/// in their hands, and a decision taken at the desk would be taken by somebody who
/// has not made it.
/// </para>
/// <para>
/// The writes cannot share a transaction — receipt in Inventory, task in Work, rows
/// here, three schemas (ADR 0002) — so the order is chosen for what a crash between
/// any two of them leaves behind. Receipt, then task, then rows: the worst case is
/// stock correctly booked into the returns bay with nothing pointing at it, which
/// is visible as a pallet in returns, and which a retried call repairs because every
/// step carries a key derived from the same root.
/// </para>
/// </summary>
public static class BookReturn
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/outbound/returns", async (
                BookReturnRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                OutboundDbContext dbContext,
                ReturnDescriber describe,
                IStockPosting posting,
                IWorkQueue work,
                IItemDirectory items,
                ILocationDirectory locations,
                IWarehouseDirectory warehouses,
                ILogger<Return> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        request, user, idempotencyKey, dbContext, describe, posting, work,
                        items, locations, warehouses, logger, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("BookReturn")
            .WithSummary("Book customer returns into the returns bay and raise the inspections.")
            .Produces<BookReturnResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<BookReturnResponse>> HandleAsync(
        BookReturnRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        OutboundDbContext dbContext,
        ReturnDescriber describe,
        IStockPosting posting,
        IWorkQueue work,
        IItemDirectory items,
        ILocationDirectory locations,
        IWarehouseDirectory warehouses,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<BookReturnResponse>(OutboundErrors.IdempotencyKeyRequired);
        }

        if (request.Lines.Count == 0)
        {
            return Result.Failure<BookReturnResponse>(OutboundErrors.NoLines);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<BookReturnResponse>(actor.Error);

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<BookReturnResponse>(OutboundErrors.WarehouseNotFound);

        var reference = request.Reference.Trim();

        var existing = await dbContext.Returns
            .Include(r => r.Lines)
            .ThenInclude(l => l.Dispositions)
            .SingleOrDefaultAsync(
                r => r.WarehouseId == warehouse.Id && r.Reference == reference, cancellationToken);

        if (existing is not null)
        {
            // Booking the same RMA twice is a desk pressing a button twice far more
            // often than it is a second pallet, and the goods are already on the
            // ledger under keys derived from this one. Handing back what is there is
            // the honest answer; posting the receipts again would double the stock.
            return new BookReturnResponse(
                await describe.DescribeAsync(existing, cancellationToken), [], InspectionsRaised: 0);
        }

        var bay = await locations.FindStorageLocationByCodeAsync(request.ToLocationCode.Trim(), cancellationToken);
        if (bay is null) return Result.Failure<BookReturnResponse>(OutboundErrors.LocationNotFound);

        // A hold bay, and nothing else. Booking a return straight into racking would
        // conserve stock perfectly while putting goods nobody has looked at where the
        // next allocation will find them — and the condition alone would not save it,
        // because a bay that holds sellable stock is a bay somebody will eventually
        // release this into by hand.
        if (bay.Role != StorageRole.Hold)
        {
            return Result.Failure<BookReturnResponse>(OutboundErrors.NotAReturnsLocation(bay.Code));
        }

        var order = await ResolveOrderAsync(dbContext, warehouse.Id, request.OrderReference, cancellationToken);
        if (order.IsFailure) return Result.Failure<BookReturnResponse>(order.Error);

        var returned = new Return
        {
            WarehouseId = warehouse.Id,
            Reference = reference,
            SalesOrderId = order.Value?.Id,
            SalesOrderReference = order.Value?.Reference,
            CustomerName = request.CustomerName ?? order.Value?.CustomerName,
            ReceivedAtLocationId = bay.Id,
            CreatedByUserName = actor.Value.UserName,
            Notes = request.Notes
        };

        var movements = new List<PostedMovement>();
        var inspections = 0;
        var lineNumber = 1;

        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0m)
            {
                return Result.Failure<BookReturnResponse>(OutboundErrors.QuantityNotPositive);
            }

            var item = await items.FindBySkuAsync(line.Sku.Trim(), cancellationToken);
            if (item is null) return Result.Failure<BookReturnResponse>(OutboundErrors.ItemNotFound);

            var orderLine = MatchOrderLine(order.Value, item.Id, line.OrderLineNumber);

            var lineKey = $"{idempotencyKey}:{lineNumber:D3}";

            var receipt = await posting.ReceiveAsync(
                new ReceiptInstruction
                {
                    ItemId = item.Id,
                    Quantity = line.Quantity,
                    ToLocationId = bay.Id,
                    LotNumber = line.LotNumber,
                    ExpiryDate = line.ExpiryDate,
                    Sscc = line.Sscc,

                    // Held, always, whatever the desk thinks of the carton. What the
                    // goods actually are is the inspection's answer, and booking a
                    // return in as Available would be this endpoint deciding the
                    // thing it exists to defer.
                    Condition = StockCondition.OnHold,
                    IsReturn = true,
                    IdempotencyKey = $"{lineKey}:receipt",
                    Actor = actor.Value,
                    Notes = request.Notes
                },
                cancellationToken);

            if (receipt.IsFailure) return Result.Failure<BookReturnResponse>(receipt.Error);

            movements.Add(receipt.Value);

            var returnLine = new ReturnLine
            {
                ReturnId = returned.Id,
                LineNumber = lineNumber,
                SalesOrderLineId = orderLine?.Id,
                ItemId = item.Id,
                Quantity = line.Quantity,
                LotId = receipt.Value.LotId,
                LotNumber = line.LotNumber,
                ExpiryDate = line.ExpiryDate,
                LicensePlateId = receipt.Value.LicensePlateId,
                ReceiptMovementId = receipt.Value.MovementId
            };

            var task = await work.EnqueueAsync(
                new WorkTaskRequest
                {
                    Kind = WorkTaskKind.Inspection,
                    WarehouseId = warehouse.Id,
                    ItemId = item.Id,
                    LotId = receipt.Value.LotId,
                    Quantity = line.Quantity,

                    // The inspection happens where the goods are standing, so the
                    // bay is both the origin and the destination. Nothing moves.
                    FromLocationId = bay.Id,
                    SuggestedLocationId = null,
                    LicensePlateId = receipt.Value.LicensePlateId,
                    Condition = StockCondition.OnHold,
                    SourceReference = order.Value is null
                        ? $"RMA {reference}"
                        : $"{order.Value.Reference}#{orderLine?.LineNumber}",
                    IdempotencyKey = $"{lineKey}:inspection"
                },
                cancellationToken);

            if (task.IsFailure) return Result.Failure<BookReturnResponse>(task.Error);

            returnLine.InspectionTaskId = task.Value;
            inspections++;

            returned.Lines.Add(returnLine);
            lineNumber++;
        }

        dbContext.Returns.Add(returned);
        await dbContext.SaveChangesAsync(cancellationToken);

        await ReturnProjection.RefreshOrderLinesAsync(dbContext, returned, cancellationToken);

        logger.LogInformation(
            "Booked return {Reference} into {Bay}: {Lines} lines, {Inspections} inspections raised by {User}",
            reference,
            bay.Code,
            returned.Lines.Count,
            inspections,
            actor.Value.UserName);

        return new BookReturnResponse(
            await describe.DescribeAsync(returned, cancellationToken), movements, inspections);
    }

    /// <summary>
    /// The order the goods came back from, when the desk named one.
    /// <para>
    /// A cancelled order is still a legitimate thing to return against — the
    /// customer was sent the goods before somebody cancelled the paperwork, and
    /// refusing the return would leave a pallet in a corner. Only a reference that
    /// names nothing at all is an error.
    /// </para>
    /// </summary>
    private static async Task<Result<SalesOrder?>> ResolveOrderAsync(
        OutboundDbContext dbContext,
        Guid warehouseId,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Result.Success<SalesOrder?>(null);
        }

        var trimmed = reference.Trim();

        var order = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(
                o => o.WarehouseId == warehouseId && o.Reference == trimmed, cancellationToken);

        return order is null
            ? Result.Failure<SalesOrder?>(OutboundErrors.OrderNotFound)
            : Result.Success<SalesOrder?>(order);
    }

    /// <summary>
    /// Which line it came back from. The named one when the desk knows, otherwise
    /// the first for that SKU — the same match a dock receipt makes against an
    /// advice note, and null when the customer sent back something that was never
    /// on the order, which is recorded as what it is rather than refused.
    /// </summary>
    private static SalesOrderLine? MatchOrderLine(SalesOrder? order, Guid itemId, int? lineNumber)
    {
        if (order is null)
        {
            return null;
        }

        return lineNumber is { } named
            ? order.Lines.SingleOrDefault(l => l.LineNumber == named)
            : order.Lines.Where(l => l.ItemId == itemId).OrderBy(l => l.LineNumber).FirstOrDefault();
    }
}
