using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
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
        endpoints.MapGet("/api/inventory/reservations/stranded", async (
                InventoryDbContext dbContext,
                IItemDirectory items,
                ILocationDirectory locations,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                // Grouped in the database: the answer is a handful of rows out of
                // however many commitments a busy warehouse is carrying, and pulling
                // them all back to filter in memory would be the wrong shape of
                // query on the one screen somebody opens when things are going badly.
                var overCommitted = await dbContext.StockReservations
                    .AsNoTracking()
                    .Where(r => r.State == ReservationState.Open)
                    .GroupBy(r => r.BalanceId)
                    .Select(group => new
                    {
                        BalanceId = group.Key,
                        Reserved = group.Sum(r => r.Quantity)
                    })
                    .Join(
                        dbContext.StockBalances.AsNoTracking(),
                        summary => summary.BalanceId,
                        balance => balance.Id,
                        (summary, balance) => new { summary.Reserved, Balance = balance })
                    .Where(row => row.Reserved > row.Balance.Quantity)
                    .ToListAsync(cancellationToken);

                if (overCommitted.Count == 0)
                {
                    return Results.Ok(Array.Empty<StrandedReservationResponse>());
                }

                var balanceIds = overCommitted.Select(row => row.Balance.Id).ToArray();

                var claims = await dbContext.StockReservations
                    .AsNoTracking()
                    .Where(r => r.State == ReservationState.Open && balanceIds.Contains(r.BalanceId))
                    .OrderBy(r => r.CreatedAt)
                    .ToListAsync(cancellationToken);

                var balances = overCommitted.ToDictionary(row => row.Balance.Id, row => row);

                var itemsById = await items.FindManyAsync(
                    [.. overCommitted.Select(row => row.Balance.ItemId).Distinct()], cancellationToken);

                var locationsById = await locations.FindManyAsync(
                    [.. overCommitted.Select(row => row.Balance.LocationId).Distinct()], cancellationToken);

                var lotIds = overCommitted
                    .Where(row => row.Balance.LotId is not null)
                    .Select(row => row.Balance.LotId!.Value)
                    .Distinct()
                    .ToArray();

                var lotsById = lotIds.Length == 0
                    ? new Dictionary<Guid, string>()
                    : await dbContext.Lots
                        .AsNoTracking()
                        .Where(l => lotIds.Contains(l.Id))
                        .ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);

                var now = clock.GetUtcNow();

                return Results.Ok(claims
                    .Select(claim =>
                    {
                        var row = balances[claim.BalanceId];
                        var balance = row.Balance;

                        return new StrandedReservationResponse(
                            claim.Id,
                            claim.OwnerReference,
                            itemsById.GetValueOrDefault(balance.ItemId)?.Sku ?? string.Empty,
                            balance.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null,
                            locationsById.GetValueOrDefault(balance.LocationId)?.Code ?? string.Empty,
                            balance.Status,
                            claim.Quantity,
                            balance.Quantity,
                            row.Reserved,
                            row.Reserved - balance.Quantity,
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
