using System.Security.Claims;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Reversals;

public sealed record ReverseMovementRequest(string? Notes = null);

/// <summary>
/// Undoes a movement the only way an append-only ledger permits: by posting its
/// mirror image.
/// <para>
/// The original entry is left exactly as it was. This is what "corrections are
/// compensating entries" means in practice, and it is strictly more informative
/// than a delete would have been — the audit trail now shows that something was
/// posted in error, who reversed it, and when, rather than showing nothing at all.
/// </para>
/// <para>
/// Reversing a reversal is refused. It would balance arithmetically and mean
/// nothing: what the user wants in that case is to re-post the original, which is
/// a new movement with its own reason.
/// </para>
/// </summary>
public static class ReverseMovement
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/movements/{movementId:guid}/reversal", async (
                Guid movementId,
                ReverseMovementRequest? request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                InventoryDbContext dbContext,
                StockLedger ledger,
                IItemDirectory items,
                ILocationDirectory locations,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        movementId, request, user, idempotencyKey,
                        dbContext, ledger, items, locations, cancellationToken))
                            .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inventory")
            .WithName("ReverseMovement")
            .WithSummary("Post the compensating entry that undoes a movement.")
            .Produces<MovementReceiptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<MovementReceiptResponse>> HandleAsync(
        Guid movementId,
        ReverseMovementRequest? request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        InventoryDbContext dbContext,
        StockLedger ledger,
        IItemDirectory items,
        ILocationDirectory locations,
        CancellationToken cancellationToken)
    {
        var actor = LedgerActor.From(user);
        if (actor.IsFailure) return Result.Failure<MovementReceiptResponse>(actor.Error);

        var original = await dbContext.StockMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == movementId, cancellationToken);

        if (original is null)
        {
            return Result.Failure<MovementReceiptResponse>(InventoryErrors.MovementNotFound);
        }

        if (original.Type == MovementType.Reversal)
        {
            return Result.Failure<MovementReceiptResponse>(InventoryErrors.CannotReverseAReversal);
        }

        // Checked here for a clear error, and again by the unique index on
        // ReversesMovementId, which is what actually holds under a double click.
        if (await dbContext.StockMovements
            .AnyAsync(m => m.ReversesMovementId == movementId, cancellationToken))
        {
            return Result.Failure<MovementReceiptResponse>(InventoryErrors.AlreadyReversed);
        }

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = original.ItemId,
                LotId = original.LotId,
                Quantity = original.Quantity,
                // The mirror: the original's destination becomes the source.
                From = new Holding(original.ToLocationId, original.ToLicensePlateId, original.ToStatus),
                To = new Holding(original.FromLocationId, original.FromLicensePlateId, original.FromStatus),
                Type = MovementType.Reversal,
                Reason = MovementReason.ReversalOfEntry,
                // Defaulted from the movement being reversed rather than required.
                // A movement can be reversed once, so its own id is a naturally
                // unique key, and a double-clicked Reverse button collapses into
                // one entry instead of two.
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? $"reversal:{movementId:D}"
                    : idempotencyKey,
                Actor = actor.Value,
                ReversesMovementId = original.Id,
                Notes = request?.Notes
            },
            cancellationToken);
        if (posted.IsFailure) return Result.Failure<MovementReceiptResponse>(posted.Error);

        // The reversal is addressed by movement id, so the codes have to be looked
        // up to echo back. Two directory calls on a supervisor-rate action, and the
        // alternative — returning a receipt with blank identifiers — would make the
        // response type lie about what it carries.
        var item = await items.FindAsync(original.ItemId, cancellationToken);

        var involved = await locations.FindManyAsync(
            [original.FromLocationId, original.ToLocationId], cancellationToken);

        var lotNumber = original.LotId is { } lotId
            ? await dbContext.Lots
                .AsNoTracking()
                .Where(l => l.Id == lotId)
                .Select(l => l.LotNumber)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return MovementReceiptResponse.From(
            posted.Value,
            item?.Sku ?? string.Empty,
            // From and To are the mirror of the original, hence the swap.
            involved.TryGetValue(original.ToLocationId, out var to) ? to.Code : string.Empty,
            involved.TryGetValue(original.FromLocationId, out var from) ? from.Code : string.Empty,
            original.ToStatus,
            original.FromStatus,
            lotNumber,
            sscc: null);
    }
}
