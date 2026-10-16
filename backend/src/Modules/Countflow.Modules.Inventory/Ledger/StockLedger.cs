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
/// Every command in this module — receipt, transfer, putaway, adjustment, status
/// change, reversal — funnels through <see cref="PostBatchAsync"/>. That is
/// deliberate: the invariants in ADR 0001 are only worth anything if there is
/// exactly one code path capable of violating them, and it is this one.
/// <see cref="PostAsync"/> is a single-command wrapper over it, not a second
/// path.
/// </para>
/// <para>
/// What happens in a posting, and why in this order:
/// <list type="number">
/// <item>Validate what can be validated without touching the database.</item>
/// <item>Look for entries already carrying these idempotency keys. A handheld on a
/// flaky link retries requests that may well have committed, so a repeat is
/// normal traffic, not an error.</item>
/// <item>Resolve the items and locations through MasterData's published
/// directory — there is no cross-schema foreign key to lean on (ADR 0002), so
/// referential integrity across the boundary is checked here.</item>
/// <item>Open a transaction, take balance-table intent and sorted plate locks,
/// then create-or-lock every touched balance <b>in a globally consistent order</b>.
/// Only then decide whether the sources have enough.</item>
/// <item>Append the movements, fold them into the balances, commit. The ledger
/// lines and the projection are written together or not at all — that is what
/// makes the projection reconstructible rather than merely usually right.</item>
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

    private const string ReservationsTable = $"{InventoryDbContext.SchemaName}.\"StockReservations\"";

    public async Task<Result<MovementReceipt>> PostAsync(
        PostingCommand command,
        CancellationToken cancellationToken)
    {
        var posted = await PostBatchAsync([command], cancellationToken);

        return posted.IsSuccess
            ? posted.Value[0]
            : Result.Failure<MovementReceipt>(posted.Error);
    }

    /// <summary>
    /// Posts several movements as one transaction.
    /// <para>
    /// Phase 2 shipped only the single-command form, and said so: a composite
    /// command has to post several entries under one idempotency key, and until
    /// putaway there was no caller that needed one. Splitting a whole-pallet move
    /// into independent calls would be the wrong answer even so — a pallet that
    /// is half moved is worse than one that is not moved at all, because the
    /// second is obvious and the first is not.
    /// </para>
    /// <para>
    /// The batch is all-or-nothing. Each entry keeps its own idempotency key, so a
    /// retry of a partially-arrived batch is still recognised entry by entry;
    /// what the transaction adds is that a partial arrival cannot happen in the
    /// first place.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<MovementReceipt>>> PostBatchAsync(
        IReadOnlyList<PostingCommand> commands,
        CancellationToken cancellationToken)
    {
        if (commands.Count == 0)
        {
            return Result.Failure<IReadOnlyList<MovementReceipt>>(InventoryErrors.NothingToPost);
        }

        foreach (var command in commands)
        {
            var basics = ValidateWithoutDatabase(command);
            if (basics.IsFailure)
            {
                return Result.Failure<IReadOnlyList<MovementReceipt>>(basics.Error);
            }
        }

        // Two entries in one batch sharing a key would collide with each other on
        // the unique index and be indistinguishable on retry. Callers derive
        // per-line keys from one root; this is the check that they actually did.
        if (commands.Select(c => c.IdempotencyKey).Distinct(StringComparer.Ordinal).Count() != commands.Count)
        {
            return Result.Failure<IReadOnlyList<MovementReceipt>>(InventoryErrors.IdempotencyKeysNotDistinct);
        }

        var resolved = await ResolveAgainstLedgerAsync(commands, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<IReadOnlyList<MovementReceipt>>(resolved.Error);
        }

        var (pending, replays) = resolved.Value;

        if (pending.Count == 0)
        {
            return await ReplayAllAsync(commands, replays, cancellationToken);
        }

        var validated = await ValidateAgainstMasterDataAsync(pending, cancellationToken);
        if (validated.IsFailure)
        {
            return Result.Failure<IReadOnlyList<MovementReceipt>>(validated.Error);
        }

        // Every module context is registered with EnableRetryOnFailure, and the
        // retrying execution strategy refuses to let application code open its own
        // transaction — it cannot replay half of one. Handing it the whole
        // transaction as a single unit is the sanctioned way round that, and it is
        // what we want anyway: a posting either lands completely or is retried
        // completely, and the idempotency keys make the retry safe.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        if (dbContext.Database.CurrentTransaction is not null)
            return await PostInTransactionAsync(commands, pending, replays, validated.Value, cancellationToken);

        return await strategy.ExecuteAsync(
            token => PostInTransactionAsync(commands, pending, replays, validated.Value, token),
            cancellationToken);
    }

    /// <summary>
    /// All physical writers take these before balance locks. Table intent precedes
    /// plate locks so a blind count's table lock cannot deadlock with a plate owner.
    /// Normal writers' ROW EXCLUSIVE locks are mutually compatible.
    /// </summary>
    internal async Task LockLicensePlatesAsync(IEnumerable<Guid> plateIds, CancellationToken ct)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Pallet locks require an Inventory transaction.");
        await dbContext.Database.ExecuteSqlRawAsync(
            """LOCK TABLE inventory."StockBalances" IN ROW EXCLUSIVE MODE""", ct);
        foreach (var id in plateIds.Distinct().Order())
        {
            // NO KEY UPDATE leaves FK key-share readers free while excluding
            // every other physical writer, including inserts of new balance rows.
            await dbContext.LicensePlates.FromSqlInterpolated(
                $"SELECT * FROM inventory.\"LicensePlates\" WHERE \"Id\" = {id} FOR NO KEY UPDATE")
                .AsNoTracking().ToListAsync(ct);
        }
    }

    private static Result ValidateWithoutDatabase(PostingCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Result.Failure(InventoryErrors.IdempotencyKeyRequired);
        }

        if (command.Quantity <= 0)
        {
            return Result.Failure(InventoryErrors.QuantityNotPositive);
        }

        return command.From == command.To
            ? Result.Failure(InventoryErrors.HoldingsIdentical)
            : Result.Success();
    }

    /// <summary>
    /// Splits the batch into what still needs writing and what is already in the
    /// ledger.
    /// <para>
    /// The common case for a retry: the first attempt landed and the client never
    /// heard about it. Cheap enough to check before doing anything else, and
    /// checked per entry so a batch that arrived halfway — which the transaction
    /// makes impossible now, but did not before, and will not if a future caller
    /// reuses one key family across two batches — still resolves correctly.
    /// </para>
    /// </summary>
    private async Task<Result<(List<PostingCommand> Pending, Dictionary<string, StockMovement> Replays)>>
        ResolveAgainstLedgerAsync(IReadOnlyList<PostingCommand> commands, CancellationToken cancellationToken)
    {
        var keys = commands.Select(c => c.IdempotencyKey).ToArray();

        var existing = await dbContext.StockMovements
            .AsNoTracking()
            .Where(m => keys.Contains(m.IdempotencyKey))
            .ToDictionaryAsync(m => m.IdempotencyKey, cancellationToken);

        var pending = new List<PostingCommand>(commands.Count);

        foreach (var command in commands)
        {
            if (!existing.TryGetValue(command.IdempotencyKey, out var already))
            {
                pending.Add(command);
                continue;
            }

            // Same key, different movement: the client reused a key it should have
            // rotated. Silently returning the old receipt would report success for
            // a movement that never happened, which is the one failure mode worse
            // than an error.
            if (command.ConsumesReservationId is { } reservationId
                && !await dbContext.StockReservations.AsNoTracking().AnyAsync(
                    reservation => reservation.Id == reservationId && reservation.ConsumedByMovementId == already.Id,
                    cancellationToken))
                return Result.Failure<(List<PostingCommand>, Dictionary<string, StockMovement>)>(InventoryErrors.IdempotencyKeyReused);

            if (!command.Matches(already))
            {
                logger.LogWarning(
                    "Idempotency key {Key} reused for a different movement than #{Sequence}",
                    command.IdempotencyKey,
                    already.Sequence);

                return Result.Failure<(List<PostingCommand>, Dictionary<string, StockMovement>)>(
                    InventoryErrors.IdempotencyKeyReused);
            }
        }

        return (pending, existing);
    }

    private async Task<Result<IReadOnlyList<MovementReceipt>>> PostInTransactionAsync(
        IReadOnlyList<PostingCommand> commands,
        IReadOnlyList<PostingCommand> pending,
        Dictionary<string, StockMovement> replays,
        IReadOnlyDictionary<Guid, LocationSnapshot> locationsById,
        CancellationToken cancellationToken)
    {
        // An attempt may be a retry, in which case the previous one left added
        // movements in the tracker. Persisting them as well would post the batch
        // twice under one key family.
        dbContext.ChangeTracker.Clear();

        var enclosing = dbContext.Database.CurrentTransaction;
        await using var ownedTransaction = enclosing is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var transaction = enclosing ?? ownedTransaction!;

        try
        {
            await LockLicensePlatesAsync(pending.SelectMany(command =>
                new[] { command.From.LicensePlateId, command.To.LicensePlateId })
                .OfType<Guid>(), cancellationToken);
            var locked = await LockEveryTouchedBalanceAsync(pending, locationsById, cancellationToken);

            // Running quantities, not the locked snapshot: two lines of one pallet
            // move can draw on the same balance row, and the second must see what
            // the first took. Without this a whole-plate move of two lots of one
            // item could pass a sufficiency check it collectively fails.
            var running = locked.ToDictionary(entry => entry.Key, entry => entry.Value.Quantity);

            var movements = new List<StockMovement>(pending.Count);

            foreach (var command in pending)
            {
                var sourceKey = new BalanceKey(command.ItemId, command.LotId, command.From);
                var source = locked[sourceKey];

                // Virtual counterparties are the contra accounts and go negative by
                // design; a shelf may not. The database enforces the same rule
                // through CK_StockBalances_NonNegativeUnlessCounterparty, but a
                // constraint violation cannot tell the operator how much is
                // actually there.
                if (!source.AllowsNegative && running[sourceKey] < command.Quantity)
                {
                    return Result.Failure<IReadOnlyList<MovementReceipt>>(
                        InventoryErrors.InsufficientStock(
                            locationsById[command.From.LocationId].Code,
                            running[sourceKey],
                            command.Quantity));
                }

                running[sourceKey] -= command.Quantity;
                running[new BalanceKey(command.ItemId, command.LotId, command.To)] += command.Quantity;

                movements.Add(ToMovement(command));
            }

            dbContext.StockMovements.AddRange(movements);

            // Added inside the delegate, after the change tracker was cleared, so
            // a retried attempt re-adds it rather than losing it. The movement it
            // belongs to is stamped on below, once the database has assigned one.
            for (var index = 0; index < pending.Count; index++)
            {
                if (pending[index].RecordsCount is { } count)
                {
                    count.AdjustmentMovementId = movements[index].Id;
                    dbContext.StockCounts.Add(count);
                }
            }

            // Written before the balances so each movement's database-assigned
            // sequence can be stamped onto the rows it touched — that is what lets
            // the reconciler say when a divergence started rather than only that
            // one exists.
            await dbContext.SaveChangesAsync(cancellationToken);

            var receipts = new List<MovementReceipt>(commands.Count);
            var posted = new Dictionary<string, MovementReceipt>(StringComparer.Ordinal);

            for (var index = 0; index < pending.Count; index++)
            {
                var command = pending[index];
                var movement = movements[index];

                // Discharged here, inside the transaction that appended the
                // movement, so the goods and the promise they were held under
                // settle together. Refusing when the claim is no longer open is
                // what stops one reservation being picked twice: the second
                // attempt finds nothing to close and takes the whole batch down
                // with it, rather than moving stock nobody had a claim on.
                if (command.ConsumesReservationId is { } reservationId
                    && !await ConsumeReservationAsync(reservationId, movement.Id, cancellationToken))
                {
                    return Result.Failure<IReadOnlyList<MovementReceipt>>(
                        InventoryErrors.ReservationNotOpen);
                }

                // Applied in command order rather than netted, so each entry's
                // reported "after" figure is the state as that entry left it —
                // which is what a confirmation screen showing the second line of a
                // pallet move has to say.
                var fromKey = new BalanceKey(command.ItemId, command.LotId, command.From);
                var toKey = new BalanceKey(command.ItemId, command.LotId, command.To);
                var source = locked[fromKey];
                var incomingAge = ReceiptAge.Incoming(movement.Type, movement.OccurredAt, source.FirstReceivedAt);
                var sourceAge = ReceiptAge.After(source.Quantity, source.FirstReceivedAt, -command.Quantity, null);
                var fromAfter = await ApplyDeltaAsync(
                    source.Id,
                    -command.Quantity,
                    movement.Sequence,
                    sourceAge,
                    cancellationToken);
                locked[fromKey] = source with { Quantity = fromAfter, FirstReceivedAt = sourceAge };

                var destination = locked[toKey];
                var destinationAge = ReceiptAge.After(destination.Quantity, destination.FirstReceivedAt, command.Quantity, incomingAge);
                var toAfter = await ApplyDeltaAsync(
                    destination.Id,
                    command.Quantity,
                    movement.Sequence,
                    destinationAge,
                    cancellationToken);
                locked[toKey] = destination with { Quantity = toAfter, FirstReceivedAt = destinationAge };

                posted[command.IdempotencyKey] = new MovementReceipt(
                    movement.Id,
                    movement.Sequence,
                    movement.OccurredAt,
                    movement.Quantity,
                    fromAfter,
                    toAfter,
                    WasAlreadyPosted: false);
            }

            if (ownedTransaction is not null) await transaction.CommitAsync(cancellationToken);

            foreach (var command in pending)
            {
                logger.LogInformation(
                    "Posted {Type} #{Sequence}: {Quantity} of item {ItemId} from {From} to {To} ({Reason}) by {User}",
                    command.Type,
                    posted[command.IdempotencyKey].Sequence,
                    command.Quantity,
                    command.ItemId,
                    locationsById[command.From.LocationId].Code,
                    locationsById[command.To.LocationId].Code,
                    command.Reason,
                    command.Actor.UserName);
            }

            // Rebuilt in the caller's order, so a batch's receipts line up with the
            // lines that were handed in even when some of them were replays.
            foreach (var command in commands)
            {
                receipts.Add(posted.TryGetValue(command.IdempotencyKey, out var fresh)
                    ? fresh
                    : await DescribeReplayAsync(command, replays[command.IdempotencyKey], cancellationToken));
            }

            return receipts;
        }
        catch (DbUpdateException exception) when (IsIdempotencyCollision(exception))
        {
            if (ownedTransaction is null)
                return Result.Failure<IReadOnlyList<MovementReceipt>>(InventoryErrors.IdempotencyKeyReused);
            // Two copies of the same retried batch arrived close enough together
            // that both got past the check above. The unique index settles it; the
            // loser reports the winner's entries, which is the same answer either
            // way.
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var winners = await ResolveAgainstLedgerAsync(commands, cancellationToken);

            return winners.IsSuccess && winners.Value.Pending.Count == 0
                ? await ReplayAllAsync(commands, winners.Value.Replays, cancellationToken)
                : Result.Failure<IReadOnlyList<MovementReceipt>>(InventoryErrors.IdempotencyKeyReused);
        }
    }

    /// <summary>
    /// Creates-or-locks every balance row the batch will touch, in one global
    /// order.
    /// <para>
    /// Lock order, not command order. Two transfers in opposite directions between
    /// the same pair of holdings would otherwise each hold the row the other is
    /// waiting for; sorting by a key every writer computes the same way means
    /// locks are always taken in one global order, so no cycle can form.
    /// </para>
    /// <para>
    /// The key extends <see cref="Holding.LockOrderKey"/> with the item and lot,
    /// because a batch touches several balance rows at one location and the
    /// holding alone no longer identifies a row. The extension is a suffix, so a
    /// single-command posting — whose two rows share an item and lot — sorts
    /// exactly as it did before, and a batch and a single command racing each
    /// other still agree on the order.
    /// </para>
    /// </summary>
    private async Task<Dictionary<BalanceKey, LockedBalance>> LockEveryTouchedBalanceAsync(
        IReadOnlyList<PostingCommand> pending,
        IReadOnlyDictionary<Guid, LocationSnapshot> locationsById,
        CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<BalanceKey, bool>();

        foreach (var command in pending)
        {
            foreach (var holding in new[] { command.From, command.To })
            {
                wanted[new BalanceKey(command.ItemId, command.LotId, holding)] =
                    locationsById[holding.LocationId].IsVirtual;
            }
        }

        var locked = new Dictionary<BalanceKey, LockedBalance>(wanted.Count);

        foreach (var (key, allowsNegative) in wanted.OrderBy(entry => entry.Key.LockOrderKey, StringComparer.Ordinal))
        {
            locked[key] = await EnsureAndLockBalanceAsync(key, allowsNegative, cancellationToken);
        }

        return locked;
    }

    private StockMovement ToMovement(PostingCommand command) => new()
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

    private async Task<Result<IReadOnlyList<MovementReceipt>>> ReplayAllAsync(
        IReadOnlyList<PostingCommand> commands,
        Dictionary<string, StockMovement> replays,
        CancellationToken cancellationToken)
    {
        var receipts = new List<MovementReceipt>(commands.Count);

        foreach (var command in commands)
        {
            receipts.Add(await DescribeReplayAsync(command, replays[command.IdempotencyKey], cancellationToken));
        }

        return receipts;
    }

    private async Task<MovementReceipt> DescribeReplayAsync(
        PostingCommand command,
        StockMovement existing,
        CancellationToken cancellationToken)
    {
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
    /// Checks every pending command against MasterData and returns the location
    /// snapshots the transaction will need, resolved once per distinct location
    /// rather than once per line.
    /// </summary>
    private async Task<Result<IReadOnlyDictionary<Guid, LocationSnapshot>>> ValidateAgainstMasterDataAsync(
        IReadOnlyList<PostingCommand> pending,
        CancellationToken cancellationToken)
    {
        var itemCache = new Dictionary<Guid, ItemSnapshot?>();
        var locationCache = new Dictionary<Guid, LocationSnapshot?>();

        foreach (var command in pending)
        {
            if (!itemCache.TryGetValue(command.ItemId, out var item))
            {
                item = await items.FindAsync(command.ItemId, cancellationToken);
                itemCache[command.ItemId] = item;
            }

            if (item is null)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.ItemNotFound);
            }

            if (!item.IsActive)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.ItemInactive);
            }

            // Tracking policy is the item's, so the ledger asks rather than assumes.
            // A lot-tracked item without a lot would produce a balance nobody can
            // rotate by expiry, which is worse than a rejected scan.
            if (item.IsLotTracked && command.LotId is null)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.LotRequired);
            }

            if (!item.IsLotTracked && command.LotId is not null)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.LotNotPermitted);
            }

            if (command.LotId is { } lotId
                && !await dbContext.Lots.AnyAsync(
                    l => l.Id == lotId && l.ItemId == command.ItemId, cancellationToken))
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.LotNotFound);
            }

            foreach (var holding in new[] { command.From, command.To })
            {
                if (!locationCache.ContainsKey(holding.LocationId))
                {
                    locationCache[holding.LocationId] =
                        await locations.FindAsync(holding.LocationId, cancellationToken);
                }
            }

            var from = locationCache[command.From.LocationId];
            var to = locationCache[command.To.LocationId];

            if (from is null || to is null)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(InventoryErrors.LocationNotFound);
            }

            // Only the destination has to be usable. Blocked racking still holds
            // stock and the whole point of blocking it is usually to get the stock
            // out, so refusing to move *from* an inactive location would trap it
            // there.
            if (!to.IsActive)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(
                    InventoryErrors.LocationBlocked(to.Code));
            }

            var shape = MovementShape.Validate(command.Type, from, to);
            if (shape.IsFailure)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(shape.Error);
            }

            if (command.Type == MovementType.StatusChange && command.From.Status == command.To.Status)
            {
                return Result.Failure<IReadOnlyDictionary<Guid, LocationSnapshot>>(
                    InventoryErrors.StatusChangeNeedsNewStatus);
            }
        }

        return Result.Success<IReadOnlyDictionary<Guid, LocationSnapshot>>(
            locationCache.ToDictionary(entry => entry.Key, entry => entry.Value!));
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
        BalanceKey key,
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
            RETURNING "Id", "Quantity", "AllowsNegative", "FirstReceivedAt"
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter("itemId", NpgsqlDbType.Uuid) { Value = key.ItemId });
        command.Parameters.Add(Nullable("lotId", key.LotId));
        command.Parameters.Add(new NpgsqlParameter("locationId", NpgsqlDbType.Uuid) { Value = key.Holding.LocationId });
        command.Parameters.Add(Nullable("licensePlateId", key.Holding.LicensePlateId));
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = (int)key.Holding.Status });
        command.Parameters.Add(new NpgsqlParameter("allowsNegative", NpgsqlDbType.Boolean) { Value = allowsNegative });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // Unreachable: the statement either inserts or updates, and both
            // return a row.
            throw new InvalidOperationException(
                $"Balance upsert returned no row for item {key.ItemId} at location {key.Holding.LocationId}.");
        }

        return new LockedBalance(
            reader.GetGuid(0),
            reader.GetDecimal(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
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
        DateTimeOffset? firstReceivedAt,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            UPDATE {BalancesTable}
            SET "Quantity" = "Quantity" + @delta,
                "LastMovementSequence" = @sequence,
                "FirstReceivedAt" = @firstReceivedAt,
                "UpdatedAt" = @now
            WHERE "Id" = @id
            RETURNING "Quantity"
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(new NpgsqlParameter("delta", NpgsqlDbType.Numeric) { Value = delta });
        command.Parameters.Add(new NpgsqlParameter("sequence", NpgsqlDbType.Bigint) { Value = sequence });
        command.Parameters.Add(new NpgsqlParameter("firstReceivedAt", NpgsqlDbType.TimestampTz) { Value = firstReceivedAt.HasValue ? firstReceivedAt.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = balanceId });

        var updated = await command.ExecuteScalarAsync(cancellationToken);

        return updated is decimal quantity
            ? quantity
            : throw new InvalidOperationException($"Balance {balanceId} vanished mid-transaction.");
    }

    /// <summary>
    /// Closes a reservation as picked, and reports whether there was one open to
    /// close.
    /// <para>
    /// The <c>State</c> predicate is the guard, not a preceding read: two picks of
    /// one reservation arriving together both pass any check made before the
    /// transaction, and the second one's <c>UPDATE</c> blocks on the row the first
    /// is holding and then matches nothing. Which is the answer — the claim was
    /// already spent.
    /// </para>
    /// <para>
    /// Reservations are locked <i>after</i> the balance rows, matching the order
    /// <c>ReservationLedger</c> takes them in. Both paths touch both tables, so
    /// the pair has to be acquired the same way round in both or they deadlock.
    /// </para>
    /// </summary>
    private async Task<bool> ConsumeReservationAsync(
        Guid reservationId,
        Guid movementId,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            UPDATE {ReservationsTable}
            SET "State" = @picked,
                "ClosedAt" = @now,
                "ConsumedByMovementId" = @movementId
            WHERE "Id" = @id AND "State" = @open
            """;

        await using var command = CreateCommand(sql);

        command.Parameters.Add(new NpgsqlParameter("picked", NpgsqlDbType.Integer) { Value = (int)ReservationState.Picked });
        command.Parameters.Add(new NpgsqlParameter("open", NpgsqlDbType.Integer) { Value = (int)ReservationState.Open });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter("movementId", NpgsqlDbType.Uuid) { Value = movementId });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = reservationId });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
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

    private readonly record struct LockedBalance(Guid Id, decimal Quantity, bool AllowsNegative, DateTimeOffset? FirstReceivedAt);
}
