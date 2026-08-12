using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.Replay;

/// <summary>
/// Master data, fetched once per run rather than once per decision.
/// <para>
/// A replay rebuilds the same warehouse two hundred times over, and the items,
/// batches and pallet labels in it barely change between one instant and the next.
/// Without this a run issues four cross-boundary lookups per decision — and cheap
/// lookups are exactly what turns into the dominant cost when something is done
/// hundreds of times in a loop.
/// </para>
/// <para>
/// <b>Everything here is present-day.</b> Item dimensions, batch expiry dates,
/// location codes and route positions are read as they are now, not as they were,
/// and there is no getting round that: none of them is versioned, so the history
/// simply is not there to read. It is also mostly harmless — a rack does not move
/// and an expiry date is a property of a batch rather than of a moment. Where it is
/// not harmless, the baseline reproduction catches it: a decision whose original
/// rules no longer choose what they chose is reported <c>Approximate</c>, and
/// master data having moved underneath it is one of the reasons why.
/// </para>
/// </summary>
public sealed class ReplayLookups(IItemDirectory items, IStockQuery stock)
{
    private readonly Dictionary<Guid, ItemSnapshot> _items = [];
    private readonly Dictionary<Guid, LotSnapshot> _lots = [];
    private readonly Dictionary<Guid, string> _plates = [];
    private readonly HashSet<Guid> _missingLots = [];
    private readonly HashSet<Guid> _missingPlates = [];

    public IReadOnlyDictionary<Guid, ItemSnapshot> Items => _items;

    public async Task<IReadOnlyDictionary<Guid, ItemSnapshot>> ItemsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.Where(id => !_items.ContainsKey(id)).Distinct().ToArray();

        if (wanted.Length > 0)
        {
            foreach (var (id, item) in await items.FindManyAsync(wanted, cancellationToken))
            {
                _items[id] = item;
            }
        }

        return _items;
    }

    public async Task<IReadOnlyDictionary<Guid, LotSnapshot>> LotsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        // Misses are remembered as well as hits. A lot id that resolves to nothing
        // would otherwise be re-requested on every one of two hundred decisions,
        // which is the one access pattern a cache can make worse rather than better.
        var wanted = ids
            .Where(id => !_lots.ContainsKey(id) && !_missingLots.Contains(id))
            .Distinct()
            .ToArray();

        if (wanted.Length > 0)
        {
            var found = await stock.FindLotsAsync(wanted, cancellationToken);

            foreach (var id in wanted)
            {
                if (found.TryGetValue(id, out var lot))
                {
                    _lots[id] = lot;
                }
                else
                {
                    _missingLots.Add(id);
                }
            }
        }

        return _lots;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> PlatesAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids
            .Where(id => !_plates.ContainsKey(id) && !_missingPlates.Contains(id))
            .Distinct()
            .ToArray();

        if (wanted.Length > 0)
        {
            var found = await stock.FindLicensePlateLabelsAsync(wanted, cancellationToken);

            foreach (var id in wanted)
            {
                if (found.TryGetValue(id, out var sscc))
                {
                    _plates[id] = sscc;
                }
                else
                {
                    _missingPlates.Add(id);
                }
            }
        }

        return _plates;
    }
}
