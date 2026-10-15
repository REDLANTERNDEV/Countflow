using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Counting;

/// <summary>Whole-bin observations and corrections settle in one Inventory transaction.</summary>
public sealed class BlindBinCounting(InventoryDbContext db, StockLedger ledger, StockCounting counting,
    StockReferenceResolver resolve, IItemDirectory items, ILocationDirectory locations, TimeProvider clock) : IBlindBinCounting
{
    public Task<long> ReadVersionAsync(Guid locationId, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Sequences are allocated before commit. Drain in-flight writers so a
            // visible later sequence cannot hide an earlier uncommitted movement.
            await db.Database.ExecuteSqlRawAsync("""LOCK TABLE inventory."StockBalances" IN SHARE ROW EXCLUSIVE MODE""", cancellationToken);
            var version = await VersionAsync(locationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return version;
        });

    public async Task<BinCountOutcome?> ReadConfirmationAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var json = await db.BinCounts.AsNoTracking().Where(record => record.TaskId == taskId).Select(record => record.Outcome).SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<BinCountOutcome>(json);
    }

    public Task<Result<BinCountOutcome>> ConfirmAsync(BinCountInstruction instruction, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(() => ConfirmCoreAsync(instruction, cancellationToken));

    private async Task<Result<BinCountOutcome>> ConfirmCoreAsync(BinCountInstruction input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 128)
            return Result.Failure<BinCountOutcome>(InventoryErrors.IdempotencyKeyRequired);
        if (input.Observed is null || input.Observed.Count > 200 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000)
            return Refuse("inventory.bin_count_invalid", "Record up to 200 holdings and a reason.");
        if (input.Observed.Any(line => line is null || string.IsNullOrWhiteSpace(line.Sku) || line.Quantity < 0
            || line.Quantity > 99999999999999.9999m || decimal.Round(line.Quantity, 4) != line.Quantity || !Enum.IsDefined(line.Condition)))
            return Refuse("inventory.bin_count_invalid", "Use nonnegative base-unit quantities with up to four decimal places, within the stock quantity range.");

        var normalized = input.Observed.Select(line => line with { Sku = line.Sku.Trim(),
            Quantity = decimal.Parse(line.Quantity.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            LotNumber = NullIfBlank(line.LotNumber), Sscc = NullIfBlank(line.Sscc) })
            .OrderBy(line => line.Sku, StringComparer.Ordinal).ThenBy(line => line.LotNumber, StringComparer.Ordinal)
            .ThenBy(line => line.Sscc, StringComparer.Ordinal).ThenBy(line => line.Condition).ThenBy(line => line.Quantity).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { input.TaskId, input.LocationId, input.ExpectedVersion, Observed = normalized, input.Actor.UserId, Reason = input.Reason.Trim() }))));

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Also excludes new holding rows. Every posting locks balances before appending
        // movements, so the version, union of holdings and batch correction stay stable.
        await db.Database.ExecuteSqlRawAsync("""LOCK TABLE inventory."StockBalances" IN SHARE ROW EXCLUSIVE MODE""", ct);
        var previous = await db.BinCounts.AsNoTracking().SingleOrDefaultAsync(record =>
            record.TaskId == input.TaskId || record.IdempotencyKey == input.IdempotencyKey, ct);
        if (previous is not null)
            return previous.TaskId == input.TaskId && previous.IdempotencyKey == input.IdempotencyKey && previous.Fingerprint == fingerprint
                ? JsonSerializer.Deserialize<BinCountOutcome>(previous.Outcome)!
                : Result.Failure<BinCountOutcome>(InventoryErrors.IdempotencyKeyReused);

        var location = await locations.FindAsync(input.LocationId, ct);
        if (location is null) return Result.Failure<BinCountOutcome>(InventoryErrors.LocationNotFound);
        if (location.IsVirtual || !location.IsActive)
            return Refuse("inventory.bin_count_invalid", "Count an active physical bin.");
        if (await VersionAsync(input.LocationId, ct) != input.ExpectedVersion)
            return Result.Failure<BinCountOutcome>(Error.Conflict("inventory.bin_changed", "Stock moved during the count. Start a fresh count."));

        var observed = new Dictionary<Key, decimal>();
        foreach (var line in normalized)
        {
            var item = await items.FindBySkuAsync(line.Sku, ct);
            if (item is null) return Result.Failure<BinCountOutcome>(InventoryErrors.ItemNotFound);
            var lot = await resolve.LotAsync(item, line.LotNumber, line.ExpiryDate, null, createIfMissing: true, ct);
            if (lot.IsFailure) return Result.Failure<BinCountOutcome>(lot.Error);
            var plate = await resolve.LicensePlateAsync(line.Sscc, createIfMissing: true, ct);
            if (plate.IsFailure) return Result.Failure<BinCountOutcome>(plate.Error);
            var key = new Key(item.Id, lot.Value, plate.Value, (InventoryStatus)line.Condition);
            if (!observed.TryAdd(key, line.Quantity))
                return Refuse("inventory.bin_count_duplicate", "Combine duplicate holdings into one observation.");
            var valid = await counting.ValidateHoldingAsync(key.Item, key.Lot, input.LocationId, ct);
            if (valid.IsFailure) return Result.Failure<BinCountOutcome>(valid.Error);
        }

        var book = await db.StockBalances.AsNoTracking().Where(balance => balance.LocationId == input.LocationId && balance.Quantity > 0)
            .ToDictionaryAsync(balance => new Key(balance.ItemId, balance.LotId, balance.LicensePlateId, balance.Status), ct);
        var keys = book.Keys.Union(observed.Keys).OrderBy(key => key.Order, StringComparer.Ordinal).ToArray();
        var counts = new List<StockCount>(); var commands = new List<PostingCommand>();
        var adjustment = await resolve.CounterpartyAsync(location.WarehouseId, CounterpartyKind.Adjustment, ct);
        if (adjustment.IsFailure) return Result.Failure<BinCountOutcome>(adjustment.Error);
        foreach (var key in keys)
        {
            var balance = book.GetValueOrDefault(key); var expected = balance?.Quantity ?? 0;
            var found = observed.GetValueOrDefault(key); var variance = found - expected;
            var count = new StockCount { WarehouseId = location.WarehouseId, ItemId = key.Item, LotId = key.Lot,
                LocationId = input.LocationId, LicensePlateId = key.Plate, Status = key.Status, ExpectedQuantity = expected,
                CountedQuantity = found, Variance = variance, AtMovementSequence = balance?.LastMovementSequence ?? 0,
                WorkTaskId = input.TaskId, IdempotencyKey = $"bin:{input.TaskId:N}:{counts.Count:D3}",
                CountedByUserId = input.Actor.UserId, CountedByUserName = input.Actor.UserName, CountedAt = clock.GetUtcNow(), Notes = input.Reason.Trim() };
            counts.Add(count);
            if (variance == 0) continue;
            var holding = new Holding(input.LocationId, key.Plate, key.Status);
            var contra = new Holding(adjustment.Value.Id, null, key.Status);
            commands.Add(new PostingCommand { ItemId = key.Item, LotId = key.Lot, Quantity = Math.Abs(variance),
                From = variance > 0 ? contra : holding, To = variance > 0 ? holding : contra,
                Type = MovementType.CountAdjustment, Reason = MovementReason.CycleCountVariance, TaskId = input.TaskId,
                IdempotencyKey = count.IdempotencyKey, Actor = new LedgerActor(input.Actor.UserId, input.Actor.UserName),
                Notes = input.Reason.Trim(), RecordsCount = count });
        }

        var receipts = new Dictionary<string, MovementReceipt>(StringComparer.Ordinal);
        if (commands.Count > 0)
        {
            var posted = await ledger.PostBatchAsync(commands, ct);
            if (posted.IsFailure) return Result.Failure<BinCountOutcome>(posted.Error);
            for (var index = 0; index < commands.Count; index++) receipts[commands[index].IdempotencyKey] = posted.Value[index];
        }
        db.StockCounts.AddRange(counts.Where(count => count.Variance == 0));
        var outcomes = counts.Select(count => new CountOutcome(count.Id, count.ExpectedQuantity, count.CountedQuantity, count.Variance,
            receipts.TryGetValue(count.IdempotencyKey, out var receipt) ? new PostedMovement(receipt.MovementId, receipt.Sequence, receipt.OccurredAt,
                receipt.Quantity, count.LotId, count.LicensePlateId, receipt.FromQuantityAfter, receipt.ToQuantityAfter, false) : null, false)).ToArray();
        var outcome = new BinCountOutcome(input.TaskId, clock.GetUtcNow(), outcomes);
        db.BinCounts.Add(new BinCountRecord { TaskId = input.TaskId, LocationId = input.LocationId, IdempotencyKey = input.IdempotencyKey,
            Fingerprint = fingerprint, Outcome = JsonSerializer.Serialize(outcome), ConfirmedByUserId = input.Actor.UserId,
            ConfirmedByUserName = input.Actor.UserName, Reason = input.Reason.Trim(), ConfirmedAt = outcome.ConfirmedAt });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return outcome;
    }

    private async Task<long> VersionAsync(Guid locationId, CancellationToken ct) =>
        await db.StockMovements.Where(m => m.FromLocationId == locationId || m.ToLocationId == locationId)
            .MaxAsync(m => (long?)m.Sequence, ct) ?? 0;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Result<BinCountOutcome> Refuse(string code, string message) => Result.Failure<BinCountOutcome>(Error.Validation(code, message));
    private readonly record struct Key(Guid Item, Guid? Lot, Guid? Plate, InventoryStatus Status)
    {
        public string Order => $"{Item:N}:{Lot:N}:{Plate:N}:{(int)Status}";
    }
}
