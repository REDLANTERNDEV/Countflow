using System.Security.Claims;
using Countflow.Modules.Inbound.Domain;
using Countflow.Modules.Inbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Inbound.Features.Receiving;

/// <param name="Quantity">
/// In the item's base unit. A handheld holding a case barcode converts first
/// through <c>/api/scan/resolve</c> — nothing past that seam reasons about
/// packaging levels.
/// </param>
/// <param name="AsnReference">
/// Optional. Present, the receipt is checked against what the supplier said was
/// coming; absent, it is a blind receipt, which is a real and common event rather
/// than an error.
/// </param>
/// <param name="Sscc">
/// The pallet label. Supplying one is what makes the putaway a whole-pallet move
/// rather than a line at a time.
/// </param>
public sealed record DockReceiptRequest(
    string Sku,
    decimal Quantity,
    string ToLocationCode,
    string? AsnReference = null,
    int? AsnLineNumber = null,
    string? LotNumber = null,
    DateOnly? ExpiryDate = null,
    DateOnly? ProductionDate = null,
    string? Sscc = null,
    StockCondition? Condition = null,
    bool? IsReturn = null,
    string? Notes = null);

/// <param name="ExpectedQuantity">What the advice note said. Null on a blind receipt.</param>
/// <param name="OutstandingQuantity">Positive is short, negative is over-delivered.</param>
public sealed record AsnLineProgress(
    string Reference,
    int LineNumber,
    decimal ExpectedQuantity,
    decimal ReceivedQuantity,
    decimal OutstandingQuantity,
    AsnStatus AsnStatus);

/// <param name="SuggestedLocationCode">
/// Where the rules say the pallet should go. Null when nothing was admissible —
/// and <paramref name="DecisionTraceId"/> then explains why, which is the whole
/// point of raising the task anyway.
/// </param>
public sealed record DockReceiptResponse(
    PostedMovement Receipt,
    string Sku,
    string Description,
    string ToLocationCode,
    string? LotNumber,
    string? Sscc,
    Guid? WorkTaskId,
    string? SuggestedLocationCode,
    Guid DecisionTraceId,
    int ConsideredCount,
    int AdmissibleCount,
    AsnLineProgress? AsnLine);

/// <summary>
/// Books goods off a truck and puts the resulting work in somebody's queue.
/// <para>
/// The whole inbound loop in one call, and every step of it goes through a
/// published contract: <see cref="IStockPosting"/> posts the receipt,
/// <see cref="IPutawayAdvisor"/> chooses the destination and writes the trace,
/// <see cref="IWorkQueue"/> raises the task. This module orchestrates; it does not
/// reimplement. That is why the ledger still has exactly one posting path after
/// Phase 3 added three modules that write to it.
/// </para>
/// <para>
/// The three writes cannot share a transaction — they are in three schemas
/// (ADR 0002) — so the order is chosen for what a crash between any two of them
/// leaves behind. Receipt, then advice, then task: the worst case is stock
/// correctly booked onto the dock with no task raised, which is visible to a
/// supervisor as a pallet sitting in receiving, and which a retried scan repairs
/// exactly because every step carries a key derived from the same root.
/// </para>
/// </summary>
public static class ReceiveAtDock
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/inbound/receipts", async (
                DockReceiptRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                InboundDbContext dbContext,
                IStockPosting posting,
                IPutawayAdvisor advisor,
                IWorkQueue work,
                IItemDirectory items,
                ILocationDirectory locations,
                TimeProvider clock,
                ILogger<Asn> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        request, user, idempotencyKey, dbContext, posting, advisor,
                        work, items, locations, clock, logger, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Inbound")
            .WithName("ReceiveAtDock")
            .WithSummary("Book goods in against an advice note and raise the putaway.")
            .Produces<DockReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<DockReceiptResponse>> HandleAsync(
        DockReceiptRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        InboundDbContext dbContext,
        IStockPosting posting,
        IPutawayAdvisor advisor,
        IWorkQueue work,
        IItemDirectory items,
        ILocationDirectory locations,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<DockReceiptResponse>(InboundErrors.IdempotencyKeyRequired);
        }

        if (request.Quantity <= 0m)
        {
            return Result.Failure<DockReceiptResponse>(InboundErrors.QuantityNotPositive);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<DockReceiptResponse>(actor.Error);

        var item = await items.FindBySkuAsync(request.Sku.Trim(), cancellationToken);
        if (item is null) return Result.Failure<DockReceiptResponse>(InboundErrors.ItemNotFound);

        var dock = await locations.FindStorageLocationByCodeAsync(request.ToLocationCode.Trim(), cancellationToken);
        if (dock is null) return Result.Failure<DockReceiptResponse>(InboundErrors.LocationNotFound);

        // A dock or a staging bay. Booking a pallet straight into racking would
        // conserve stock perfectly while skipping the check-and-putaway loop this
        // endpoint exists to run — and the putaway movement, and its decision
        // trace, are exactly what would go missing.
        if (dock.Role is not (StorageRole.Dock or StorageRole.Staging))
        {
            return Result.Failure<DockReceiptResponse>(InboundErrors.NotAReceivingLocation(dock.Code));
        }

        var line = await ResolveAsnLineAsync(dbContext, request, item, cancellationToken);
        if (line.IsFailure) return Result.Failure<DockReceiptResponse>(line.Error);

        // Every write in this handler derives its key from the caller's, so a
        // retry re-runs the whole loop and each step recognises its own earlier
        // attempt independently.
        var receipt = await posting.ReceiveAsync(
            new ReceiptInstruction
            {
                ItemId = item.Id,
                Quantity = request.Quantity,
                ToLocationId = dock.Id,
                LotNumber = request.LotNumber ?? line.Value?.ExpectedLotNumber,
                ExpiryDate = request.ExpiryDate ?? line.Value?.ExpectedExpiryDate,
                ProductionDate = request.ProductionDate,
                Sscc = request.Sscc,
                Condition = request.Condition ?? StockCondition.Available,
                IsReturn = request.IsReturn ?? false,
                IdempotencyKey = $"{idempotencyKey}:receipt",
                Actor = actor.Value,
                Notes = request.Notes
            },
            cancellationToken);

        if (receipt.IsFailure) return Result.Failure<DockReceiptResponse>(receipt.Error);

        var progress = line.Value is null
            ? null
            : await RecordAgainstAsnAsync(
                dbContext, line.Value, receipt.Value, request.Quantity,
                $"{idempotencyKey}:receipt", actor.Value, clock.GetUtcNow(), cancellationToken);

        var advice = await advisor.SuggestAsync(
            new PutawayQuery
            {
                WarehouseId = dock.WarehouseId,
                ItemId = item.Id,
                LotId = receipt.Value.LotId,
                Quantity = request.Quantity,
                FromLocationId = dock.Id,
                LicensePlateId = receipt.Value.LicensePlateId,
                Condition = request.Condition ?? StockCondition.Available,
                Actor = actor.Value
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<DockReceiptResponse>(advice.Error);

        var task = await work.EnqueueAsync(
            new WorkTaskRequest
            {
                Kind = WorkTaskKind.Putaway,
                WarehouseId = dock.WarehouseId,
                ItemId = item.Id,
                LotId = receipt.Value.LotId,
                Quantity = request.Quantity,
                FromLocationId = dock.Id,
                SuggestedLocationId = advice.Value.LocationId,
                LicensePlateId = receipt.Value.LicensePlateId,
                DecisionTraceId = advice.Value.DecisionTraceId,
                Condition = request.Condition ?? StockCondition.Available,
                SourceReference = progress is null ? null : $"{progress.Reference}#{progress.LineNumber}",
                IdempotencyKey = $"{idempotencyKey}:putaway"
            },
            cancellationToken);

        if (task.IsFailure) return Result.Failure<DockReceiptResponse>(task.Error);

        if (advice.Value.LocationId is null)
        {
            // Worth a warning on its own: a receipt the rules cannot place means
            // the warehouse is full, misconfigured, or the item's master data is
            // wrong, and all three want a human before the pallets stack up.
            logger.LogWarning(
                "No admissible putaway location for {Sku} at {Dock}; task {TaskId} raised without a suggestion (trace {TraceId})",
                item.Sku,
                dock.Code,
                task.Value,
                advice.Value.DecisionTraceId);
        }

        return new DockReceiptResponse(
            receipt.Value,
            item.Sku,
            item.Description,
            dock.Code,
            request.LotNumber ?? line.Value?.ExpectedLotNumber,
            request.Sscc,
            task.Value,
            advice.Value.LocationCode,
            advice.Value.DecisionTraceId,
            advice.Value.ConsideredCount,
            advice.Value.AdmissibleCount,
            progress);
    }

    private static async Task<Result<AsnLine?>> ResolveAsnLineAsync(
        InboundDbContext dbContext,
        DockReceiptRequest request,
        ItemSnapshot item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AsnReference))
        {
            return Result.Success<AsnLine?>(null);
        }

        var reference = request.AsnReference.Trim();

        var asn = await dbContext.Asns
            .Include(a => a.Lines)
            .SingleOrDefaultAsync(a => a.Reference == reference, cancellationToken);

        if (asn is null) return Result.Failure<AsnLine?>(InboundErrors.AsnNotFound);

        if (asn.Status is AsnStatus.Cancelled or AsnStatus.Closed)
        {
            return Result.Failure<AsnLine?>(InboundErrors.AsnNotOpen(reference));
        }

        if (request.AsnLineNumber is { } lineNumber)
        {
            var named = asn.Lines.SingleOrDefault(l => l.LineNumber == lineNumber);
            return named is null
                ? Result.Failure<AsnLine?>(InboundErrors.AsnLineNotFound)
                : Result.Success<AsnLine?>(named);
        }

        // No line number: match on the SKU, which is what the operator scanned.
        // Several lines for one SKU is legal — different batches on one note — so
        // the first with something still outstanding wins, and a fully-received
        // SKU falls back to the last line so an over-delivery still books against
        // something rather than being refused at the dock.
        var forItem = asn.Lines.Where(l => l.ItemId == item.Id).OrderBy(l => l.LineNumber).ToList();

        if (forItem.Count == 0)
        {
            return Result.Failure<AsnLine?>(InboundErrors.ItemNotOnAsn(item.Sku, reference));
        }

        return Result.Success<AsnLine?>(
            forItem.FirstOrDefault(l => l.OutstandingQuantity > 0) ?? forItem[^1]);
    }

    /// <summary>
    /// Appends the booking and re-derives the line's running total from what has
    /// actually landed. See <see cref="AsnReceipt"/> for why it is not an
    /// increment.
    /// </summary>
    private static async Task<AsnLineProgress> RecordAgainstAsnAsync(
        InboundDbContext dbContext,
        AsnLine line,
        PostedMovement movement,
        decimal quantity,
        string idempotencyKey,
        PostingActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var alreadyBooked = await dbContext.AsnReceipts
            .AnyAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

        if (!alreadyBooked)
        {
            dbContext.AsnReceipts.Add(new AsnReceipt
            {
                AsnLineId = line.Id,
                IdempotencyKey = idempotencyKey,
                Quantity = quantity,
                MovementId = movement.MovementId,
                ReceivedAt = now,
                ReceivedByUserName = actor.UserName
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        line.ReceivedQuantity = await dbContext.AsnReceipts
            .Where(r => r.AsnLineId == line.Id)
            .SumAsync(r => r.Quantity, cancellationToken);

        var asn = line.Asn!;

        // Status is derived, never set by hand. An advice note is "receiving"
        // because something has been received against it, not because somebody
        // remembered to press a button.
        asn.Status = asn.Lines.All(l => l.ReceivedQuantity >= l.ExpectedQuantity)
            ? AsnStatus.Complete
            : AsnStatus.Receiving;

        if (asn.Status == AsnStatus.Complete)
        {
            asn.ClosedAt ??= now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new AsnLineProgress(
            asn.Reference,
            line.LineNumber,
            line.ExpectedQuantity,
            line.ReceivedQuantity,
            line.OutstandingQuantity,
            asn.Status);
    }
}
