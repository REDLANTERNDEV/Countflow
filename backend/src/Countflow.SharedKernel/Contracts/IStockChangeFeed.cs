using System.Text.Json;
namespace Countflow.SharedKernel.Contracts;

/// <summary>Published ledger feed. Opaque JSON entries preserve the public movement contract.</summary>
public interface IStockChangeFeed
{
    Task<StockChangeBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken);
}
public sealed record StockChangeBatch(IReadOnlyList<JsonElement> Changes, long NextCursor, bool HasMore, long Watermark);
