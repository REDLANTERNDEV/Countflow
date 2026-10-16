using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published write surface for commitments (ADR 0002).
/// <para>
/// An adapter over <see cref="ReservationLedger"/>, and nothing more — the same
/// relationship <see cref="StockPosting"/> has to <c>StockLedger</c>, for the same
/// reason. A second place that decided whether stock may be promised would be a
/// second place that could decide wrongly, and the availability check is the only
/// thing standing between an eager sales team and the same pallet sold twice.
/// </para>
/// </summary>
public sealed class StockReservations(ReservationLedger reservations, StrandedReservations stranded) : IStockReservations
{
    public async Task<Result<ReservedStock>> ReserveAsync(
        ReservationInstruction instruction,
        CancellationToken cancellationToken)
    {
        var reserved = await reservations.ReserveAsync(
            instruction.BalanceId,
            instruction.Quantity,
            instruction.OwnerId,
            instruction.OwnerReference,
            instruction.DecisionTraceId,
            instruction.IdempotencyKey,
            new LedgerActor(instruction.Actor.UserId, instruction.Actor.UserName),
            cancellationToken);

        if (reserved.IsFailure)
        {
            return Result.Failure<ReservedStock>(reserved.Error);
        }

        var (reservation, wasAlready) = reserved.Value;

        // A freshly written claim has no holding loaded — it never needed one, and
        // the reservation deliberately does not repeat the coordinate. The caller
        // does need it, so it is read back here rather than denormalised there.
        var described = reservation.Balance is null
            ? await reservations.FindAsync(reservation.Id, cancellationToken) ?? reservation
            : reservation;

        return Describe(described, wasAlready);
    }

    public async Task<Result<int>> ReleaseAsync(
        IReadOnlyCollection<Guid> reservationIds,
        string reason,
        CancellationToken cancellationToken) =>
        await reservations.ReleaseAsync(reservationIds, reason, cancellationToken);

    public async Task<IReadOnlyList<ReservedStock>> FindOpenByOwnerAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var open = await reservations.FindOpenByOwnerAsync(ownerId, cancellationToken);

        return [.. open.Select(reservation => Describe(reservation, wasAlreadyReserved: true))];
    }

    public async Task<IReadOnlyList<StrandedStock>> FindStrandedByOwnerAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var rows = await stranded.FindAsync(ownerId, cancellationToken);
        return [.. rows.Select(row => new StrandedStock(
            Describe(row.Reservation, wasAlreadyReserved: true),
            row.Reservation.Balance!.Quantity,
            row.TotalReserved))];
    }

    private static ReservedStock Describe(StockReservation reservation, bool wasAlreadyReserved) =>
        new(
            reservation.Id,
            reservation.BalanceId,
            reservation.Balance!.ItemId,
            reservation.Balance.LotId,
            reservation.Balance.LocationId,
            reservation.Balance.LicensePlateId,
            reservation.Quantity,
            reservation.OwnerId,
            wasAlreadyReserved);
}
