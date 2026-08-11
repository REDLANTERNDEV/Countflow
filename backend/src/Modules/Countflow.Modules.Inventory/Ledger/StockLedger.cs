using System.Data.Common;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// The only way stock moves.
/// <para>
/// Every command in this module — receipt, transfer, adjustment, status change,
/// reversal — funnels through <see cref="PostAsync"/>. That is deliberate: the
/// invariants in ADR 0001 are only worth anything if there is exactly one code
/// path capable of violating them, and it is this one.
/// </para>
/// <para>
/// What happens in a posting, and why in this order:
/// <list type="number">
/// <item>Validate what can be validated without touching the database.</item>
/// <item>Look for an entry already carrying this idempotency key. A handheld on a
/// flaky link retries requests that may well have committed, so a repeat is
/// normal traffic, not an error.</item>
/// <item>Resolve the item and both locations through MasterData's published
/// directory — there is no cross-schema foreign key to lean on (ADR 0002), so
/// referential integrity across the boundary is checked here.</item>
/// <item>Open a transaction, create-or-lock both balance rows <b>in a globally
/// consistent order</b>, and only then decide whether the source has enough.</item>
/// <item>Append the movement, fold it into both balances, commit. The ledger line
/// and the projection are written together or not at all — that is what makes the
/// projection reconstructible rather than merely usually right.</item>
/// </list>
/// </para>
/// <para>
/// Isolation stays at the connection default (read committed). Serializable would
/// buy nothing here and cost retries: the rows this needs to be stable are locked
/// explicitly with <c>FOR UPDATE</c> semantics before the sufficiency check, which
/// is the narrower and cheaper guarantee.
/// </para>
/// </summary>
public sealed class StockLedger(
    InventoryDbContext dbContext,
    IItemDirectory items,
    ILocationDirectory locations,
    TimeProvider clock,
    ILogger<StockLedger> logger)
{
    private const string BalancesTable = $"{InventoryDbContext.SchemaName}.\"StockBalances\"";

    public async Task<Result<MovementReceipt>> PostAsync(
        PostingCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Result.Failure<MovementReceipt>(InventoryErrors.IdempotencyKeyRequired);
        }

        if (command.Quantity <= 0)
        {
            return Result.Failure<MovementReceipt>(InventoryErrors.QuantityNotPositive);
        }

        if (command.From == command.To)
        {
            return Result.Failure<MovementReceipt>(InventoryErrors.HoldingsIdentical);
        }

        // The common case for a retry: the first attempt landed and the client
        // never heard about it. Cheap enough to check before doing anything else.
        var alreadyPosted = await FindByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
        if (alreadyPosted is not null)
        {
            return await ReplayAsync(command, alreadyPosted, cancellationToken);
        }

        var validated = await ValidateAgainstMasterDataAsync(command, cancellationToken);
        if (validated.IsFailure)
        {
            return Result.Failure<MovementReceipt>(validated.Error);
        }

        var (from, to) = validated.Value;

        // Every module context is registered with EnableRetryOnFailure, and the
        // retrying execution strategy refuses to let application code open its own
        // transaction — it cannot replay half of one. Handing it the whole
        // transaction as a single unit is the sanctioned way round that, and it is
        // what we want anyway: a posting either lands completely or is retried
        // completely, and the idempotency key makes the retry safe.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            token => PostInTransactionAsync(command, from, to, token),
            cancellationToken);
    }

    private async Task<Result<MovementReceipt>> PostInTransactionAsync(
        PostingCommand command,
        LocationSnapshot from,
        LocationSnapshot to,
        CancellationToken cancellationToken)
    {
        // An attempt may be a retry, in which case the previous one left an added
        // movement in the tracker. Persisting it as well would post the command
        // twice under one key.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Lock order, not command order. Two transfers in opposite directions
            // between the same pair of holdings would otherwise each hold the row
            // the other is waiting for; sorting by a key both writers compute the
            // same way means locks are always taken in one global order, so no
            // cycle can form. See Holding.LockOrderKey.
            var sides = new[]
                {
                    (Holding: command.From, Location: from),
                    (Holding: command.To, Location: to)
                }
                .OrderBy(side => side.Holding.LockOrderKey, StringComparer.Ordinal);

            var locked = new Dictionary<Holding, LockedBalance>();
            foreach (var side in sides)
            {
                locked[side.Holding] = await EnsureAndLockBalanceAsync(
                    command.ItemId,
                    command.LotId,
                    side.Holding,
                    side.Location.IsVirtual,
                    cancellationToken);
            }

            var source = locked[command.From];

            // Virtual counterparties are the contra accounts and go negative by
            // design; a shelf may not. The database enforces the same rule through
            // CK_StockBalances_NonNegativeUnlessCounterparty, but a constraint
            // violation cannot tell the operator how much is actually there.
            if (!source.AllowsNegative && source.Quantity < command.Quantity)
            {
                return Result.Failure<MovementReceipt>(
                    InventoryErrors.InsufficientStock(from.Code, source.Quantity, command.Quantity));
            }

            var movement = new StockMovement
            {
                OccurredAt = clock.GetUtcNow(),
                ItemId = command.ItemId,
                LotId = command.LotId,
                Quantity = command.Quantity,
                FromLocationId = command.From.LocationId,
                FromLicensePlateId = command.From.LicensePlateId,
                FromStatus = command.From.Status,
                ToLocationId = command.To.LocationId,
                ToLicensePlateId = command.To.LicensePlateId,
                ToStatus = command.To.Status,
                Type = command.Type,
                Reason = command.Reason,
                IdempotencyKey = command.IdempotencyKey,
                TaskId = command.TaskId,
                DecisionTraceId = command.DecisionTraceId,
                ReversesMovementId = command.ReversesMovementId,
                UserId = command.Actor.UserId,
                UserName = command.Actor.UserName,
                Notes = command.Notes
            };

            dbContext.StockMovements.Add(movement);

            // Written before the balances so the movement's database-assigned
            // sequence can be stamped onto both rows — that is what lets the
            // reconciler say when a divergence started rather than only that one
            // exists.
            await dbContext.SaveChangesAsync(cancellationToken);

            var fromAfter = await ApplyDeltaAsync(
                source.Id, -command.Quantity, movement.Sequence, cancellationToken);

            var toAfter = await ApplyDeltaAsync(
                locked[command.To].Id, command.Quantity, movement.Sequence, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Posted {Type} #{Sequence}: {Quantity} of item {ItemId} from {From} to {To} ({Reason}) by {User}",
                command.Type,
                movement.Sequence,
                command.Quantity,
                command.ItemId,
                from.Code,
                to.Code,
                command.Reason,
                command.Actor.UserName);

            return new MovementReceipt(
                movement.Id,
                movement.Sequence,
                movement.OccurredAt,
                movement.Quantity,
                fromAfter,
                toAfter,
                WasAlreadyPosted: false);
        }
        catch (DbUpdateException exception) when (IsIdempotencyCollision(exception))
        {
            // Two copies of the same retried command arrived close enough together
            // that both got past the check above. The unique index settles it; the
            // loser reports the winner's entry, which is the same answer either
            // way.
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var winner = await FindByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);

            return winner is null
                ? Result.Failure<MovementReceipt>(InventoryErrors.IdempotencyKeyReused)
                : await ReplayAsync(command, winner, cancellationToken);
        }
    }

    /// <summary>
    /// Current quantity of one holding. Public because the enquiry endpoints and
    /// the reversal command both need it and neither should reimplement the
    /// null-safe key comparison.
    /// </summary>
    public async Task<decimal> ReadQuantityAsync(
        Guid itemId,
        Guid? lotId,
        Holding holding,
        CancellationToken cancellationToken)
    {
        var balance = await MatchBalance(itemId, lotId, holding)
            .Select(b => (decimal?)b.Quantity)
            .SingleOrDefaultAsync(cancellationToken);

        return balance ?? 0m;
    }

    private async Task<Result<(LocationSnapshot From, LocationSnapshot To)>> ValidateAgainstMasterDataAsync(
        PostingCommand command,
        CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(command.ItemId, cancellationToken);
        if (item is null)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.ItemNotFound);
        }

        if (!item.IsActive)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.ItemInactive);
        }

        // Tracking policy is the item's, so the ledger asks rather than assumes.
        // A lot-tracked item without a lot would produce a balance nobody can
        // rotate by expiry, which is worse than a rejected scan.
        if (item.IsLotTracked && command.LotId is null)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.LotRequired);
        }

        if (!item.IsLotTracked && command.LotId is not null)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.LotNotPermitted);
        }

        if (command.LotId is { } lotId
            && !await dbContext.Lots.AnyAsync(
                l => l.Id == lotId && l.ItemId == command.ItemId, cancellationToken))
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.LotNotFound);
        }

        var from = await locations.FindAsync(command.From.LocationId, cancellationToken);
        var to = await locations.FindAsync(command.To.LocationId, cancellationToken);

        if (from is null || to is null)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.LocationNotFound);
        }

        // Only the destination has to be usable. Blocked racking still holds stock
        // and the whole point of blocking it is usually to get the stock out, so
        // refusing to move *from* an inactive location would trap it there.
        if (!to.IsActive)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(InventoryErrors.LocationBlocked(to.Code));
        }

        var shape = MovementShape.Validate(command.Type, from, to);
        if (shape.IsFailure)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(shape.Error);
        }

        if (command.Type == MovementType.StatusChange && command.From.Status == command.To.Status)
        {
            return Result.Failure<(LocationSnapshot, LocationSnapshot)>(
                InventoryErrors.StatusChangeNeedsNewStatus);
        }

        return (from, to);
    }

    private Task<StockMovement?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        dbContext.StockMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.IdempotencyKey == key, cancellationToken);

    private async Task<Result<MovementReceipt>> ReplayAsync(
        PostingCommand command,
        StockMovement existing,
        CancellationToken cancellationToken)
    {
        // Same key, different movement: the client reused a key it should have
        // rotated. Silently returning the old receipt would report success for a
        // movement that never happened, which is the one failure mode worse than
        // an error.
        if (!command.Matches(existing))
        {
            logger.LogWarning(
                "Idempotency key {Key} reused for a different movement than #{Sequence}",
                command.IdempotencyKey,
                existing.Sequence);

            return Result.Failure<MovementReceipt>(InventoryErrors.IdempotencyKeyReused);
        }

        logger.LogInformation(
            "Idempotent replay of {Key}; movement #{Sequence} already posted",
            command.IdempotencyKey,
            existing.Sequence);

        // Reported as they stand now, not as they stood when the movement landed.
        // Anything else would need a point-in-time read on every retry to tell the
        // caller something it cannot act on.
        return new MovementReceipt(
            existing.Id,
            existing.Sequence,
            existing.OccurredAt,
            existing.Quantity,
            await ReadQuantityAsync(command.ItemId, command.LotId, command.From, cancellationToken),
            await ReadQuantityAsync(command.ItemId, command.LotId, command.To, cancellationToken),
            WasAlreadyPosted: true);
    }

    /// <summary>
    /// Creates the balance row if it is missing and takes a row lock on it either
    /// way, returning what it currently holds.
    /// <para>
    /// One statement rather than an insert-then-select pair, because the pair has
    /// a race between its halves: two postings against a holding that does not
    /// exist yet would both find nothing and both insert. <c>ON CONFLICT DO
    /// UPDATE</c> resolves that inside Postgres, and — unlike <c>DO NOTHING</c> —
    /// it locks the conflicting row, which is what the sufficiency check needs to
    /// be able to trust its answer.
    /// </para>
    /// </summary>
    private async Task<LockedBalance> EnsureAndLockBalanceAsync(
        Guid itemId,
        Guid? lotId,
        Holding holding,
        bool allowsNegative,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            INSERT INTO {BalancesTable}
                ("Id", "ItemId", "LotId", "LocationId", "LicensePlateId", "Status",
                 "Quantity", "AllowsNegative", "LastMovementSequence", "UpdatedAt")
            VALUES (@id, @itemId, @lotId, @locationId, @licensePlateId, @status,
                    0, @allowsNegative, 0, @now)
            ON CONFLICT ("ItemId", "LotId", "LocationId", "LicensePlateId", "Status")
            DO UPDATE SET "UpdatedAt" = EXCLUDED."UpdatedAt"
            RETURNING "Id", "Quantity", "AllowsNegative"
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter("itemId", NpgsqlDbType.Uuid) { Value = itemId });
        command.Parameters.Add(Nullable("lotId", lotId));
        command.Parameters.Add(new NpgsqlParameter("locationId", NpgsqlDbType.Uuid) { Value = holding.LocationId });
        command.Parameters.Add(Nullable("licensePlateId", holding.LicensePlateId));
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = (int)holding.Status });
        command.Parameters.Add(new NpgsqlParameter("allowsNegative", NpgsqlDbType.Boolean) { Value = allowsNegative });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // Unreachable: the statement either inserts or updates, and both
            // return a row.
            throw new InvalidOperationException(
                $"Balance upsert returned no row for item {itemId} at location {holding.LocationId}.");
        }

        return new LockedBalance(
            reader.GetGuid(0),
            reader.GetDecimal(1),
            reader.GetBoolean(2));
    }

    /// <summary>
    /// Folds a movement into one balance row, returning the resulting quantity.
    /// Safe as a blind <c>Quantity + delta</c> because the caller already holds the
    /// row lock from <see cref="EnsureAndLockBalanceAsync"/>.
    /// </summary>
    private async Task<decimal> ApplyDeltaAsync(
        Guid balanceId,
        decimal delta,
        long sequence,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            UPDATE {BalancesTable}
            SET "Quantity" = "Quantity" + @delta,
                "LastMovementSequence" = @sequence,
                "UpdatedAt" = @now
            WHERE "Id" = @id
            RETURNING "Quantity"
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(new NpgsqlParameter("delta", NpgsqlDbType.Numeric) { Value = delta });
        command.Parameters.Add(new NpgsqlParameter("sequence", NpgsqlDbType.Bigint) { Value = sequence });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = balanceId });

        var updated = await command.ExecuteScalarAsync(cancellationToken);

        return updated is decimal quantity
            ? quantity
            : throw new InvalidOperationException($"Balance {balanceId} vanished mid-transaction.");
    }

    /// <summary>
    /// A command on the context's own connection, enlisted in its current
    /// transaction.
    /// <para>
    /// Hand-written SQL rather than LINQ for these two statements: <c>ON CONFLICT
    /// … DO UPDATE … RETURNING</c> is the atomic upsert-and-lock the concurrency
    /// argument above depends on, and EF has no expression for it. Going through
    /// the context's connection keeps them inside the same transaction as the
    /// movement insert, which is the part that actually matters.
    /// </para>
    /// </summary>
    private DbCommand CreateCommand(string sql)
    {
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    private static NpgsqlParameter Nullable(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid)
        {
            // DBNull rather than null, and an explicit type either way: Postgres
            // cannot infer the type of an untyped null parameter inside
            // IS NOT DISTINCT FROM or an INSERT list, and fails the statement.
            Value = value.HasValue ? value.Value : DBNull.Value
        };

    /// <summary>
    /// Matches exactly one balance row, with null-safe comparison on the two
    /// optional key parts. Written out rather than relying on <c>==</c> because a
    /// null lot must match a null lot, and the SQL for that is not the SQL for
    /// equality.
    /// </summary>
    private IQueryable<StockBalance> MatchBalance(Guid itemId, Guid? lotId, Holding holding) =>
        dbContext.StockBalances
            .AsNoTracking()
            .Where(b =>
                b.ItemId == itemId
                && ((b.LotId == null && lotId == null) || b.LotId == lotId)
                && b.LocationId == holding.LocationId
                && ((b.LicensePlateId == null && holding.LicensePlateId == null)
                    || b.LicensePlateId == holding.LicensePlateId)
                && b.Status == holding.Status);

    private static bool IsIdempotencyCollision(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        } postgres
        && postgres.ConstraintName?.Contains("IdempotencyKey", StringComparison.Ordinal) == true;

    private readonly record struct LockedBalance(Guid Id, decimal Quantity, bool AllowsNegative);
}
