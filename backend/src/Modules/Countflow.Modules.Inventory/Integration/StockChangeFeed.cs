using System.Text.Json;
using System.Text.Json.Serialization;
using Countflow.Modules.Inventory.Features.Enquiry;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
namespace Countflow.Modules.Inventory.Integration;

public sealed class StockChangeFeed(InventoryDbContext db, IItemDirectory items, ILocationDirectory locations, TimeProvider clock) : IStockChangeFeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public async Task<StockChangeBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var page = await GetLedgerChanges.ReadAsync(db, items, locations, clock, afterSequence, limit, cancellationToken);
        return new(page.Changes.Select(row => JsonSerializer.SerializeToElement(row, Json)).ToList(), page.NextCursor, page.HasMore, page.Watermark);
    }
}
