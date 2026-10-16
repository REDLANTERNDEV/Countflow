using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Accuracy;

/// <param name="Shortfall">
/// How much of the promise the holding can no longer keep, across every open claim
/// standing against it. Reported per reservation so the person fixing it can see
/// which orders are competing for what is left.
/// </param>
public sealed record StrandedReservationResponse(
    Guid ReservationId,
    string OwnerReference,
    string Sku,
    string? LotNumber,
    string LocationCode,
    InventoryStatus Status,
    decimal ReservedQuantity,
    decimal OnHandQuantity,
    decimal TotalReserved,
    decimal Shortfall,
    DateTimeOffset CreatedAt,
    int AgeDays);

/// <summary>
/// Commitments standing against stock that is no longer there.
/// <para>
/// Deferred out of Phase 4 with the note that "loudly is the right failure" — an
/// ad-hoc transfer can move stock out from under an open claim, and the pick then
/// fails with insufficient stock rather than silently re-allocating somebody else's
/// order. That is still the right behaviour at the moment of the pick. What was
/// missing is anyone finding out before the picker does, and this is where it
/// surfaces: a promise the warehouse cannot keep is an accuracy problem in exactly
/// the sense a miscount is, and it belongs on the same screen.
/// </para>
/// <para>
/// <b>Over-commitment is the signal, not a missing balance row.</b> Balance rows
/// are never deleted, so a holding whose stock has been moved away is a row reading
/// zero rather than a row that has gone — the claims against it are all still
/// resolvable and all worthless. Comparing the sum of open claims against what is
/// on hand catches that case and the subtler one it shares a cause with: two
/// claims that were each fine when made and no longer fit together because a
/// transfer took half the pallet.
/// </para>
/// </summary>
public static class GetStrandedReservations
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/reservations/stranded", async (
                InventoryDbContext dbContext,
                StrandedReservations stranded,
                IItemDirectory items,
                ILocationDirectory locations,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var rows = await stranded.FindAsync(ownerId: null, cancellationToken);
                if (rows.Count == 0)
                    return Results.Ok(Array.Empty<StrandedReservationResponse>());

                var balances = rows.Select(row => row.Reservation.Balance!).DistinctBy(balance => balance.Id).ToList();
                var itemsById = await items.FindManyAsync(
                    [.. balances.Select(balance => balance.ItemId).Distinct()], cancellationToken);
                var locationsById = await locations.FindManyAsync(
                    [.. balances.Select(balance => balance.LocationId).Distinct()], cancellationToken);

                var lotIds = balances
                    .Where(balance => balance.LotId is not null)
                    .Select(balance => balance.LotId!.Value)
                    .Distinct()
                    .ToArray();

                var lotsById = lotIds.Length == 0
                    ? new Dictionary<Guid, string>()
                    : await dbContext.Lots
                        .AsNoTracking()
                        .Where(l => lotIds.Contains(l.Id))
                        .ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);

                var now = clock.GetUtcNow();

                return Results.Ok(rows
                    .Select(row =>
                    {
                        var claim = row.Reservation;
                        var balance = claim.Balance!;

                        return new StrandedReservationResponse(
                            claim.Id,
                            claim.OwnerReference,
                            itemsById.GetValueOrDefault(balance.ItemId)?.Sku ?? string.Empty,
                            balance.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null,
                            locationsById.GetValueOrDefault(balance.LocationId)?.Code ?? string.Empty,
                            balance.Status,
                            claim.Quantity,
                            balance.Quantity,
                            row.TotalReserved,
                            row.TotalReserved - balance.Quantity,
                            claim.CreatedAt,
                            (int)(now - claim.CreatedAt).TotalDays);
                    })
                    // Oldest first. A commitment that has been stranded for a week is
                    // an order somebody has already been asked about twice.
                    .OrderBy(response => response.CreatedAt)
                    .ToArray());
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inventory")
            .WithName("GetStrandedReservations")
            .WithSummary("Open commitments standing against holdings that can no longer meet them.")
            .Produces<IReadOnlyList<StrandedReservationResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}
