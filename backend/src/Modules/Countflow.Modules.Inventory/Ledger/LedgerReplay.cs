using System.Data;
using System.Data.Common;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Narrows a replay to the holdings a caller asked about.
/// <para>
/// Applied twice, on purpose. The movement stream is filtered first so Postgres
/// scans an index rather than the whole ledger, and the summed rows are filtered
/// again — because a movement that touches the requested location on one side
/// necessarily touches some other holding on the other, and that other holding is
/// not part of the answer.
/// </para>
/// </summary>
public sealed record ReplayFilter(
    Guid? ItemId = null,
    Guid? LotId = null,
    Guid? LocationId = null,
    Guid? LicensePlateId = null)
{
    public bool IsEmpty =>
        ItemId is null && LotId is null && LocationId is null && LicensePlateId is null;
}

/// <summary>One holding's quantity as derived from the ledger alone.</summary>
public sealed record ReplayedBalance(
    Guid ItemId,
    Guid? LotId,
    Guid LocationId,
    Guid? LicensePlateId,
    InventoryStatus Status,
    decimal Quantity);

/// <summary>
/// Rebuilds balances by summing the ledger.
/// <para>
/// One implementation serves two jobs that look different and are not:
/// <b>reconciliation</b> replays everything and compares the result against the
/// stored projection, and <b>point-in-time enquiry</b> replays up to a cutoff and
/// reports the result directly. "Show me A-07-03-02 as of 14 July" and "prove the
/// balances are right" are the same query with a different bound.
/// </para>
/// <para>
/// Sharing the code is also what makes the reconciler trustworthy: the summation
/// it checks the projection against is exercised by every point-in-time test as
/// well, so it cannot quietly rot into agreeing with a bug.
/// </para>
/// </summary>
public sealed class LedgerReplay(InventoryDbContext dbContext)
{
    private const string Movements = $"{InventoryDbContext.SchemaName}.\"StockMovements\"";

    public Task<IReadOnlyList<ReplayedBalance>> ReplayAsync(
        long? asOfSequence,
        CancellationToken cancellationToken) =>
        ReplayAsync(asOfSequence, filter: null, cancellationToken);

    /// <summary>
    /// Sums every movement — optionally only those at or before
    /// <paramref name="asOfSequence"/> — into one balance per holding.
    /// <para>
    /// Each movement contributes twice: a credit to its destination holding and an
    /// equal debit to its source. That is the whole of double entry, and it is why
    /// the quantities this returns always sum to exactly zero.
    /// </para>
    /// <para>
    /// The cutoff is a <b>sequence number</b>, not a timestamp. Several movements
    /// land in the same millisecond during a wave, and "the balance at
    /// 14:32:07.421" has to have one answer. Callers holding a clock time resolve
    /// it first with <see cref="ResolveSequenceAsync"/>.
    /// </para>
    /// <para>
    /// Hand-written SQL rather than LINQ. EF cannot translate a <c>Concat</c> of
    /// two projections into a constructor — it fails with "unable to translate set
    /// operation after client projection" — and the alternative, pulling the ledger
    /// into memory to sum it, works at seed scale and collapses on the first real
    /// month of history. The UNION ALL below is also simply the clearest statement
    /// of what a replay *is*.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReplayedBalance>> ReplayAsync(
        long? asOfSequence,
        ReplayFilter? filter,
        CancellationToken cancellationToken)
    {
        filter ??= new ReplayFilter();

        var sql = $"""
            SELECT "ItemId", "LotId", "LocationId", "LicensePlateId", "Status", SUM("Quantity") AS "Quantity"
            FROM (
                SELECT "ItemId", "LotId",
                       "ToLocationId"     AS "LocationId",
                       "ToLicensePlateId" AS "LicensePlateId",
                       "ToStatus"         AS "Status",
                       "Quantity"
                FROM {Movements}
                WHERE ({SequenceBound})
                  AND ({MovementFilter})
                UNION ALL
                SELECT "ItemId", "LotId",
                       "FromLocationId"     AS "LocationId",
                       "FromLicensePlateId" AS "LicensePlateId",
                       "FromStatus"         AS "Status",
                       -"Quantity"
                FROM {Movements}
                WHERE ({SequenceBound})
                  AND ({MovementFilter})
            ) AS postings
            WHERE (@locationId IS NULL OR "LocationId" = @locationId)
              AND (@licensePlateId IS NULL OR "LicensePlateId" = @licensePlateId)
            GROUP BY "ItemId", "LotId", "LocationId", "LicensePlateId", "Status"
            """;

        var connection = dbContext.Database.GetDbConnection();

        // The reconciler calls this inside a repeatable-read transaction and owns
        // the connection; a point-in-time enquiry calls it with nothing open. Only
        // close what we opened.
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

            command.Parameters.Add(new NpgsqlParameter("asOfSequence", NpgsqlDbType.Bigint)
            {
                Value = asOfSequence.HasValue ? asOfSequence.Value : DBNull.Value
            });

            command.Parameters.Add(Uuid("itemId", filter.ItemId));
            command.Parameters.Add(Uuid("lotId", filter.LotId));
            command.Parameters.Add(Uuid("locationId", filter.LocationId));
            command.Parameters.Add(Uuid("licensePlateId", filter.LicensePlateId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var balances = new List<ReplayedBalance>();

            while (await reader.ReadAsync(cancellationToken))
            {
                balances.Add(new ReplayedBalance(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    (InventoryStatus)reader.GetInt32(4),
                    reader.GetDecimal(5)));
            }

            return balances;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// The last sequence number posted at or before a wall-clock instant, or null
    /// if the ledger did not exist yet.
    /// </summary>
    public Task<long?> ResolveSequenceAsync(DateTimeOffset asOf, CancellationToken cancellationToken) =>
        dbContext.StockMovements
            .AsNoTracking()
            .Where(m => m.OccurredAt <= asOf)
            .MaxAsync(m => (long?)m.Sequence, cancellationToken);

    /// <summary>Null means "no bound", not "matches null" — hence the guard rather than a plain equality.</summary>
    private const string SequenceBound = """@asOfSequence IS NULL OR "Sequence" <= @asOfSequence""";

    /// <summary>
    /// Prunes the movement stream before it is unioned. Location and licence plate
    /// match on <i>either</i> side, because a holding's history is everything that
    /// arrived and everything that left.
    /// </summary>
    private const string MovementFilter = """
        (@itemId IS NULL OR "ItemId" = @itemId)
                  AND (@lotId IS NULL OR "LotId" = @lotId)
                  AND (@locationId IS NULL OR "FromLocationId" = @locationId OR "ToLocationId" = @locationId)
                  AND (@licensePlateId IS NULL
                       OR "FromLicensePlateId" = @licensePlateId
                       OR "ToLicensePlateId" = @licensePlateId)
        """;

    private static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid)
        {
            // Explicitly typed even when null: Postgres cannot infer the type of a
            // bare null parameter and fails the whole statement.
            Value = value.HasValue ? value.Value : DBNull.Value
        };
}
