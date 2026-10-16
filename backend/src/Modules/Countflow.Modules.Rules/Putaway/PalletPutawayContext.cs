using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// One load at one origin. Each line carries occupancy relative to its item/lot;
/// the location-level occupancy must come from the same warehouse snapshot.
/// No rule fetches data, so this context is usable for live decisions and replay.
/// </summary>
public sealed class PalletPutawayContext
{
    public IReadOnlyList<PutawayContext> Lines { get; }

    /// <summary>
    /// For load-level rules only (location, travel, cube and weight). The item
    /// fields remain those of the first line; item-specific rules use Lines.
    /// Unknown dimensions poison the total rather than understating the load.
    /// </summary>
    internal PutawayContext WholeLoad { get; }

    public PalletPutawayContext(IReadOnlyList<PutawayContext> lines)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lines.Count);
        if (lines.Any(line => line.Quantity <= 0m || line.OriginLocationId != lines[0].OriginLocationId))
            throw new ArgumentException("A load needs positive quantities at one origin.", nameof(lines));

        Lines = Array.AsReadOnly(lines.OrderBy(line => line.Item.Id)
            .ThenBy(line => line.LotId).ThenBy(line => line.Condition).ToArray());
        WholeLoad = Lines[0] with
        {
            RequiredVolumeM3 = SumKnown(Lines.Select(line => line.RequiredVolumeM3)),
            RequiredWeightKg = SumKnown(Lines.Select(line => line.RequiredWeightKg))
        };
    }

    /// <summary>The live advisor and historical replay build exactly the same context.</summary>
    public static Result<PalletPutawayContext> Create(
        IReadOnlyList<PlateLine> contents,
        Guid originLocationId,
        IReadOnlyDictionary<Guid, ItemSnapshot> items,
        IReadOnlyDictionary<StockSubject, IReadOnlyDictionary<Guid, LocationOccupancy>> occupancy,
        RouteScale route)
    {
        if (contents.Count == 0 || contents.Any(line => line.Quantity <= 0m))
            return Result.Failure<PalletPutawayContext>(RulesErrors.QuantityNotPositive);
        var lines = new List<PutawayContext>();
        foreach (var line in contents)
        {
            if (!items.TryGetValue(line.ItemId, out var item))
                return Result.Failure<PalletPutawayContext>(RulesErrors.ItemNotFound);
            lines.Add(new PutawayContext(item, line.LotId, line.Quantity, line.Condition,
                originLocationId, occupancy[new StockSubject(line.ItemId, line.LotId)],
                item.UnitVolumeM3 * line.Quantity, item.UnitWeightKg * line.Quantity, route));
        }
        return new PalletPutawayContext(lines);
    }

    private static decimal? SumKnown(IEnumerable<decimal?> values)
    {
        decimal total = 0m;
        foreach (var value in values)
        {
            if (value is null) return null;
            total += value.Value;
        }
        return total;
    }
}
