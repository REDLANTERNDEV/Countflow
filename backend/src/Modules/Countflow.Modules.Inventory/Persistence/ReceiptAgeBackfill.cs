using Countflow.SharedKernel.Modules;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Inventory.Persistence;

public sealed class ReceiptAgeBackfill(InventoryDbContext db, IStockHistory history, TimeProvider clock) : IModuleSeeder
{
    public int Order => -100;
    public Task SeedAsync(CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(() => RebuildAsync(cancellationToken));

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.ReceiptAgeProjectionStates.FromSqlRaw(
            """SELECT * FROM inventory."ReceiptAgeProjectionState" WHERE "Id" = 1 FOR UPDATE""")
            .AsNoTracking().SingleAsync(cancellationToken);
        if (state.Completed) { await transaction.CommitAsync(cancellationToken); return; }

        // Posting locks balances before it appends movements. This blocks writers while
        // the one-time reconstruction reads its stable ledger and updates the projection.
        await db.Database.ExecuteSqlRawAsync("""LOCK TABLE inventory."StockBalances" IN SHARE ROW EXCLUSIVE MODE""", cancellationToken);
        var balances = await db.StockBalances.AsNoTracking().Select(balance => new { balance.Id, balance.LocationId, balance.Quantity }).ToListAsync(cancellationToken);
        var horizon = await db.StockMovements.MaxAsync(movement => (DateTimeOffset?)movement.OccurredAt, cancellationToken) ?? clock.GetUtcNow();
        var reconstructed = new Dictionary<Guid, HistoricalHolding>();
        await foreach (var snapshot in history.WalkAsync([.. balances.Select(balance => balance.LocationId).Distinct()], [horizon], cancellationToken))
            foreach (var holding in snapshot.Holdings) reconstructed[holding.BalanceId] = holding;

        await db.Database.ExecuteSqlRawAsync("""CREATE TEMP TABLE "ReceiptAgeBackfillValues" ("Id" uuid PRIMARY KEY, "FirstReceivedAt" timestamptz) ON COMMIT DROP""", cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var writer = await connection.BeginBinaryImportAsync(
            """COPY "ReceiptAgeBackfillValues" ("Id", "FirstReceivedAt") FROM STDIN (FORMAT BINARY)""", cancellationToken))
        {
            foreach (var balance in balances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A drifted quantity is not evidence of the receipt age of its contents.
                var age = reconstructed.TryGetValue(balance.Id, out var holding) && holding.Quantity == balance.Quantity && balance.Quantity > 0
                    ? holding.FirstReceivedAt : null;
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(balance.Id, NpgsqlDbType.Uuid, cancellationToken);
                if (age is { } received) await writer.WriteAsync(received, NpgsqlDbType.TimestampTz, cancellationToken);
                else await writer.WriteNullAsync(cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE inventory."StockBalances" AS balance
            SET "FirstReceivedAt" = rebuilt."FirstReceivedAt"
            FROM "ReceiptAgeBackfillValues" AS rebuilt WHERE balance."Id" = rebuilt."Id"
            """, cancellationToken);
        await db.ReceiptAgeProjectionStates.Where(value => value.Id == 1).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.Completed, true).SetProperty(value => value.CompletedAt, clock.GetUtcNow()), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
