using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Enquiry;

/// <param name="Changes">Ledger entries in ascending sequence order. Never re-ordered, never repeated.</param>
/// <param name="NextCursor">Send this back as <c>afterSequence</c>. Unchanged when the page is empty.</param>
/// <param name="HasMore">
/// True when more entries are already readable. False means caught up to the
/// watermark, which is the signal to wait rather than to poll again immediately.
/// </param>
/// <param name="Watermark">
/// The highest sequence this feed will currently hand out. Diagnostic: the
/// distance between the cursor and the watermark is how far behind a consumer is.
/// </param>
public sealed record LedgerChangePage(
    IReadOnlyList<StockMovementResponse> Changes,
    long NextCursor,
    bool HasMore,
    long Watermark);

/// <summary>
/// Change data capture over the stock ledger, with no outbox and no second copy
/// of anything.
/// <para>
/// This endpoint exists because <see cref="Domain.StockMovement.Sequence"/> is
/// already a total order over every change inventory has ever undergone (ADR
/// 0001). An integrator maintaining their own projection needs "what changed since
/// I last looked", and the honest answer here is an ascending cursor over that
/// sequence — no event table to keep in step with the ledger, no publisher to
/// fail silently, and no possibility of the feed disagreeing with the truth
/// because <i>the feed is the truth, read in a different order</i>.
/// </para>
/// <para>
/// It is a <b>read</b>, and that is the load-bearing part. The temptation with a
/// public integration surface is to grow a parallel write path for integrators;
/// Inventory has exactly one posting path (<c>StockLedger.PostAsync</c>) and this
/// phase does not make it two.
/// </para>
/// <para>
/// It sits beside <c>/inventory/movements</c> rather than replacing it. That one
/// pages <i>descending</i> and filters, because a person reading a stock card
/// wants the most recent thing first; a consumer rebuilding state wants the oldest
/// unseen thing first and wants nothing filtered out from under it.
/// </para>
/// </summary>
public static class GetLedgerChanges
{
    /// <summary>
    /// How far behind the newest entry the feed deliberately runs.
    /// <para>
    /// <b>This is the correctness of the whole endpoint, so it is worth the
    /// paragraph.</b> A Postgres identity column hands out sequence numbers at
    /// <c>INSERT</c>, not at <c>COMMIT</c>. Two concurrent postings can therefore
    /// take 105 and 106 and commit in the other order, which means a naïve reader
    /// can see 106, advance its cursor past it, and never be shown 105 — a
    /// consumer's projection silently diverging from a ledger that is itself
    /// perfectly correct. That is the same class of bug as a double-posted retry,
    /// and it is invisible until somebody argues about stock.
    /// </para>
    /// <para>
    /// So the feed serves only up to a watermark: the highest sequence among
    /// entries whose <c>OccurredAt</c> is at least this old. <c>OccurredAt</c> is
    /// stamped inside the posting transaction just before the insert, so if the
    /// watermark row began at least <c>lag</c> ago, every sequence below it was
    /// handed out earlier still and its transaction has long since committed or
    /// rolled back. The bound needs <c>lag</c> to exceed twice the longest posting
    /// transaction; these are single-digit milliseconds, so five seconds is four
    /// orders of magnitude of headroom.
    /// </para>
    /// <para>
    /// Note it is a filter on <b>sequence</b>, not on <c>OccurredAt</c>. Filtering
    /// on the timestamp directly would reintroduce the same hole from the other
    /// side, because the row that lost the race can carry the later timestamp.
    /// </para>
    /// <para>
    /// Permanent gaps are fine and expected — a posting refused at the unique
    /// index on the idempotency key burns a sequence value on its way out. The
    /// contract is ascending and complete, never contiguous.
    /// </para>
    /// </summary>
    private static readonly TimeSpan StabilityLag = TimeSpan.FromSeconds(5);

    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/changes", async (
                InventoryDbContext dbContext,
                IItemDirectory items,
                ILocationDirectory locations,
                TimeProvider clock,
                CancellationToken cancellationToken,
                long afterSequence = 0,
                int limit = DefaultLimit) =>
            {
                return Results.Ok(await ReadAsync(dbContext, items, locations, clock, afterSequence, limit, cancellationToken));
            })
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("GetLedgerChanges")
            .WithSummary("Read the stock ledger forwards from a cursor — every change, in order, exactly once.")
            .Produces<LedgerChangePage>();
    }
    internal static async Task<LedgerChangePage> ReadAsync(InventoryDbContext dbContext, IItemDirectory items,
        ILocationDirectory locations, TimeProvider clock, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        afterSequence = Math.Max(afterSequence, 0);

        var settled = clock.GetUtcNow() - StabilityLag;

        var watermark = await dbContext.StockMovements
            .AsNoTracking()
            .Where(movement => movement.OccurredAt <= settled)
            .MaxAsync(movement => (long?)movement.Sequence, cancellationToken) ?? 0;

        if (watermark <= afterSequence)
        {
            return new LedgerChangePage([], afterSequence, HasMore: false, watermark);
        }

        var query = dbContext.StockMovements
            .AsNoTracking()
            .Where(movement => movement.Sequence > afterSequence && movement.Sequence <= watermark);

        // One extra row rather than a COUNT. The consumer only needs to know
        // whether to come straight back, and counting the whole tail of a
        // ledger to answer a yes/no question gets slower every day the
        // warehouse runs.
        var movements = await query
            .OrderBy(movement => movement.Sequence)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = movements.Count > limit;
        if (hasMore)
        {
            movements.RemoveAt(movements.Count - 1);
        }

        var rows = await GetStockMovements.DescribeAsync(
            dbContext, items, locations, movements, cancellationToken);

        var cursor = movements.Count == 0 ? afterSequence : movements[^1].Sequence;

        return new LedgerChangePage(rows, cursor, hasMore, watermark);
    }

}
