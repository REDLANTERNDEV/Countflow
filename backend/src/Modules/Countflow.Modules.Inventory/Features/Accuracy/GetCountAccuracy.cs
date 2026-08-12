using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Accuracy;

/// <summary>
/// Accuracy for one slice of the warehouse.
/// </summary>
/// <param name="Counted">Holdings counted in the window. The denominator, and the number that makes the rate honest.</param>
/// <param name="Accurate">Counts where the shelf agreed with the book.</param>
/// <param name="NetVariance">
/// Signed sum of the variances, in base units. Near zero with a large
/// <paramref name="AbsoluteVariance"/> is the interesting shape: it means the
/// warehouse is not losing stock, it is filing it in the wrong places.
/// </param>
/// <param name="AbsoluteVariance">Sum of the absolute variances. What was actually wrong, regardless of direction.</param>
public sealed record AccuracySlice(
    string Key,
    int Counted,
    int Accurate,
    decimal NetVariance,
    decimal AbsoluteVariance)
{
    /// <summary>
    /// Counts that agreed, over counts taken. Null rather than 100% when nothing was
    /// counted — an unmeasured slice reported as perfect is the exact failure this
    /// screen exists to prevent.
    /// </summary>
    public decimal? AccuracyRate =>
        Counted == 0 ? null : Math.Round((decimal)Accurate / Counted, 4);
}

/// <param name="ByZone">
/// Where the errors are. A single zone carrying the warehouse's whole variance is
/// a layout or a labelling problem rather than a discipline problem, and the two
/// have entirely different fixes.
/// </param>
/// <param name="ByClass">
/// Accuracy by velocity band. The number a supervisor is actually judged on: an
/// A-class item is touched a hundred times more often than a C, so equal accuracy
/// across the bands means the fast-moving stock is being handled far better than
/// the slow.
/// </param>
public sealed record CountAccuracyResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    AccuracySlice Overall,
    IReadOnlyList<AccuracySlice> ByZone,
    IReadOnlyList<AccuracySlice> ByClass);

/// <summary>
/// How much of what the system says about the stock has been verified, and how
/// much of that turned out to be right.
/// <para>
/// The number the product's central claim is made on, so it is worth being precise
/// about what it counts. The denominator is <b>counts taken</b>, not holdings in
/// the warehouse — a bay nobody has looked at is not accurate, it is unknown, and
/// folding the unknown in as a pass is how every WMS on the market reports 99.8%.
/// Coverage is a separate question, answered by the count plans rather than here.
/// </para>
/// <para>
/// Aggregated in memory over the counts rather than in SQL, and that is not
/// laziness. Zone and velocity band are MasterData's facts and this schema cannot
/// join to them (ADR 0002), so the grouping keys arrive through the published
/// directories. It is affordable because of what is being counted: a warehouse
/// takes a few thousand counts a year against a ledger that grows by that much in
/// a week, and this is emphatically the small table.
/// </para>
/// </summary>
public static class GetCountAccuracy
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/accuracy", async (
                Guid warehouseId,
                DateTimeOffset? from,
                DateTimeOffset? to,
                InventoryDbContext dbContext,
                IItemDirectory items,
                ILocationDirectory locations,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var now = clock.GetUtcNow();

                // Ninety days by default: long enough that a C-class item on a
                // six-month cycle appears at all, short enough that a rule change
                // made last quarter is not still being averaged in.
                var since = from ?? now.AddDays(-90);
                var until = to ?? now;

                var counts = await dbContext.StockCounts
                    .AsNoTracking()
                    .Where(c => c.WarehouseId == warehouseId
                                && c.CountedAt >= since
                                && c.CountedAt <= until)
                    .OrderBy(c => c.CountedAt)
                    .Select(c => new Row(c.ItemId, c.LocationId, c.Variance))
                    .ToListAsync(cancellationToken);

                if (counts.Count == 0)
                {
                    return Results.Ok(new CountAccuracyResponse(
                        since, until, Empty("overall"), [], []));
                }

                var itemsById = await items.FindManyAsync(
                    [.. counts.Select(c => c.ItemId).Distinct()], cancellationToken);

                var locationsById = await locations.FindStorageLocationsAsync(
                    [.. counts.Select(c => c.LocationId).Distinct()], cancellationToken);

                return Results.Ok(new CountAccuracyResponse(
                    since,
                    until,
                    Slice("overall", counts),
                    Group(counts, row =>
                        locationsById.GetValueOrDefault(row.LocationId)?.ZoneCode ?? "—"),
                    Group(counts, row =>
                        (itemsById.GetValueOrDefault(row.ItemId)?.AbcBand ?? AbcBand.C).ToString())));
            })
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("GetCountAccuracy")
            .WithSummary("Count accuracy by zone and velocity band over a window.")
            .Produces<CountAccuracyResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static List<AccuracySlice> Group(IReadOnlyList<Row> counts, Func<Row, string> key) =>
    [
        .. counts
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => Slice(group.Key, [.. group]))
            // Worst first. A dashboard sorted alphabetically buries the one row
            // anybody opened it to find.
            .OrderBy(slice => slice.AccuracyRate ?? 1m)
            .ThenBy(slice => slice.Key, StringComparer.Ordinal)
    ];

    private static AccuracySlice Slice(string key, IReadOnlyList<Row> counts) =>
        new(
            key,
            counts.Count,
            counts.Count(c => c.Variance == 0m),
            counts.Sum(c => c.Variance),
            counts.Sum(c => Math.Abs(c.Variance)));

    private static AccuracySlice Empty(string key) => new(key, 0, 0, 0m, 0m);

    private sealed record Row(Guid ItemId, Guid LocationId, decimal Variance);
}
