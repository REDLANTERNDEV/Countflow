using System.Data.Common;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Inventory.Reservations;

/// <summary>
/// The only way stock is promised to anything.
/// <para>
/// The ledger's sibling, and written to the same shape for the same reasons.
/// Every claim is idempotent, every claim is checked against a locked balance
/// row, and the locks are taken in <see cref="BalanceKey.LockOrderKey"/> order —
/// the same order <see cref="StockLedger"/> takes them in, because both paths
/// touch balances and reservations and two writers that disagreed about the order
/// would deadlock the moment a warehouse got busy.
/// </para>
/// <para>
/// What it deliberately does not do is move anything. A reservation changes no
/// quantity anywhere: it changes what the <i>next</i> question about that quantity
/// is allowed to answer. Available-to-promise is on-hand minus the open claims,
/// computed rather than stored, so there is no second number to drift from the
/// first.
/// </para>
/// </summary>
public sealed class ReservationLedger(
    InventoryDbContext dbContext,
    TimeProvider clock,
    ILogger<ReservationLedger> logger)
{
    private const string BalancesTable = $"{InventoryDbContext.SchemaName}.\"StockBalances\"";

    /// <summary>
    /// A claim, and whether making it wrote anything.
    /// </summary>
    /// <param name="WasAlreadyReserved">
    /// True when this key had already been used for this same claim. Reported for
    /// the same reason the ledger reports it: the caller treats it as a success —
    /// that is the point of the key — but a developer chasing a duplicated order
    /// release can see what actually happened.
    /// </param>
    public readonly record struct ReservationOutcome(
        StockReservation Reservation,
        bool WasAlreadyReserved);

    public async Task<Result<ReservationOutcome>> ReserveAsync(
        Guid balanceId,
        decimal quantity,
        Guid ownerId,
        string ownerReference,
        Guid? decisionTraceId,
        string idempotencyKey,
        LedgerActor actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReservationOutcome>(InventoryErrors.IdempotencyKeyRequired);
        }

        if (quantity <= 0m)
        {
            return Result.Failure<ReservationOutcome>(InventoryErrors.QuantityNotPositive);
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            token => ReserveInTransactionAsync(
                balanceId, quantity, ownerId, ownerReference, decisionTraceId, idempotencyKey, actor, token),
            cancellationToken);
    }

    private async Task<Result<ReservationOutcome>> ReserveInTransactionAsync(
        Guid balanceId,
        decimal quantity,
        Guid ownerId,
        string ownerReference,
        Guid? decisionTraceId,
        string idempotencyKey,
        LedgerActor actor,
        CancellationToken cancellationToken)
    {
        // A retry left the previous attempt's entity in the tracker; saving it
        // again would insert a second claim under one key.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var already = await dbContext.StockReservations
                .AsNoTracking()
                .Include(r => r.Balance)
                .SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

            if (already is not null)
            {
                // Same key, different claim: the caller reused a key it should
                // have rotated. Handing back the old reservation would report that
                // stock is held for an order it is not held for.
                return already.BalanceId == balanceId
                       && already.Quantity == quantity
                       && already.OwnerId == ownerId
                    ? new ReservationOutcome(already, WasAlreadyReserved: true)
                    : Result.Failure<ReservationOutcome>(InventoryErrors.IdempotencyKeyReused);
            }

            // Locked before the sum, and the sum taken inside the same lock. Two
            // orders reaching for the last pallet at the same instant would
            // otherwise both read the same availability and both commit; the row
            // lock is what makes the second one see the first one's claim.
            var locked = await LockBalanceAsync(balanceId, cancellationToken);
            if (locked is not { } onHand)
            {
                return Result.Failure<ReservationOutcome>(InventoryErrors.HoldingNotFound);
            }

            var reserved = await dbContext.StockReservations
                .Where(r => r.BalanceId == balanceId && r.State == ReservationState.Open)
                .SumAsync(r => r.Quantity, cancellationToken);

            var available = onHand - reserved;

            if (quantity > available)
            {
                return Result.Failure<ReservationOutcome>(
                    InventoryErrors.InsufficientAvailableStock(available, quantity));
            }

            var reservation = new StockReservation
            {
                BalanceId = balanceId,
                Quantity = quantity,
                OwnerId = ownerId,
                OwnerReference = ownerReference,
                DecisionTraceId = decisionTraceId,
                IdempotencyKey = idempotencyKey,
                UserId = actor.UserId,
                UserName = actor.UserName,
                CreatedAt = clock.GetUtcNow()
            };

            dbContext.StockReservations.Add(reservation);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Reserved {Quantity} of holding {BalanceId} for {Owner} ({Available} was available, trace {TraceId})",
                quantity,
                balanceId,
                ownerReference,
                available,
                decisionTraceId);

            return new ReservationOutcome(reservation, WasAlreadyReserved: false);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Two copies of one retried release arrived close enough together that
            // both got past the check above. The unique index settles it and the
            // loser reports the winner's claim, which is the same answer either
            // way.
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var winner = await dbContext.StockReservations
                .AsNoTracking()
                .Include(r => r.Balance)
                .SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

            return winner is null
                ? Result.Failure<ReservationOutcome>(InventoryErrors.IdempotencyKeyReused)
                : new ReservationOutcome(winner, WasAlreadyReserved: true);
        }
    }

    /// <summary>
    /// Gives claims back.
    /// <para>
    /// Idempotent by construction: the update only touches rows that are still
    /// open, so releasing something already picked or already released is a
    /// no-op rather than an error. The caller's intent — "this should not be
    /// standing" — is satisfied in every one of those cases, and a cancelled order
    /// retrying its cancellation should not be told off.
    /// </para>
    /// </summary>
    public async Task<int> ReleaseAsync(
        IReadOnlyCollection<Guid> reservationIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (reservationIds.Count == 0)
        {
            return 0;
        }

        var ids = reservationIds.Distinct().ToArray();
        var now = clock.GetUtcNow();

        var released = await dbContext.StockReservations
            .Where(r => ids.Contains(r.Id) && r.State == ReservationState.Open)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(r => r.State, ReservationState.Released)
                    .SetProperty(r => r.ClosedAt, now)
                    .SetProperty(r => r.ReleaseReason, reason),
                cancellationToken);

        if (released > 0)
        {
            logger.LogInformation("Released {Count} reservations: {Reason}", released, reason);
        }

        return released;
    }

    /// <summary>Open claims for one owner, with the holdings they stand against.</summary>
    public async Task<IReadOnlyList<StockReservation>> FindOpenByOwnerAsync(
        Guid ownerId,
        CancellationToken cancellationToken) =>
        await dbContext.StockReservations
            .AsNoTracking()
            .Include(r => r.Balance)
            .Where(r => r.OwnerId == ownerId && r.State == ReservationState.Open)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<StockReservation?> FindAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await dbContext.StockReservations
            .AsNoTracking()
            .Include(r => r.Balance)
            .SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

    /// <summary>
    /// How much of each holding is spoken for. One grouped query for a page of
    /// balances rather than one per row, which is what keeps the stock screen's
    /// third column free.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> OpenByBalanceAsync(
        IReadOnlyCollection<Guid> balanceIds,
        CancellationToken cancellationToken)
    {
        if (balanceIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var ids = balanceIds.Distinct().ToArray();

        return await dbContext.StockReservations
            .AsNoTracking()
            .Where(r => ids.Contains(r.BalanceId) && r.State == ReservationState.Open)
            .GroupBy(r => r.BalanceId)
            .Select(group => new { BalanceId = group.Key, Quantity = group.Sum(r => r.Quantity) })
            .ToDictionaryAsync(row => row.BalanceId, row => row.Quantity, cancellationToken);
    }

    /// <summary>
    /// Takes the row lock the availability check depends on.
    /// <para>
    /// <c>FOR UPDATE</c> on the balance row rather than on the reservations,
    /// because the reservations being counted do not exist yet — the row this has
    /// to serialise on is the one both writers agree exists, and the balance is
    /// it. Missing means the holding has never held anything, which reads as zero
    /// available and refuses the claim.
    /// </para>
    /// </summary>
    private async Task<decimal?> LockBalanceAsync(Guid balanceId, CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT "Quantity"
            FROM {BalancesTable}
            WHERE "Id" = @id
            FOR UPDATE
            """;

        await using var command = CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = balanceId });

        return await command.ExecuteScalarAsync(cancellationToken) as decimal?;
    }

    private DbCommand CreateCommand(string sql)
    {
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
