using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Reservations;

/// <summary>
/// The shared definition of a stranded claim, for the supervisor report and
/// modules asking about their own commitments. One SQL statement observes the
/// claims and their holding together; an owner filter never narrows the sum.
/// This is a diagnostic snapshot, not a lock or authorization to release stock.
/// </summary>
public sealed class StrandedReservations(InventoryDbContext dbContext)
{
    public async Task<IReadOnlyList<StrandedReservation>> FindAsync(
        Guid? ownerId,
        CancellationToken cancellationToken)
    {
        var totals = dbContext.StockReservations
            .Where(reservation => reservation.State == ReservationState.Open)
            .GroupBy(reservation => reservation.BalanceId)
            .Select(group => new { BalanceId = group.Key, Quantity = group.Sum(reservation => reservation.Quantity) });

        var claims = dbContext.StockReservations
            .AsNoTracking()
            .Include(reservation => reservation.Balance)
            .Where(reservation => reservation.State == ReservationState.Open);

        if (ownerId is { } owner)
            claims = claims.Where(reservation => reservation.OwnerId == owner);

        return await (
                from claim in claims
                join total in totals on claim.BalanceId equals total.BalanceId
                where total.Quantity > claim.Balance!.Quantity
                orderby claim.CreatedAt, claim.Id
                select new StrandedReservation(claim, total.Quantity))
            .ToListAsync(cancellationToken);
    }
}

public sealed record StrandedReservation(StockReservation Reservation, decimal TotalReserved);
