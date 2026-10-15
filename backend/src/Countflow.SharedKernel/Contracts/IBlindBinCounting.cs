using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>One physically observed holding. Quantities are in the item's base unit.</summary>
public sealed record BinCountObservation(string Sku, decimal Quantity, string? LotNumber = null,
    DateOnly? ExpiryDate = null, string? Sscc = null, StockCondition Condition = StockCondition.Available);

public sealed record BinCountInstruction(Guid TaskId, Guid LocationId, long ExpectedVersion,
    IReadOnlyList<BinCountObservation> Observed, string IdempotencyKey, PostingActor Actor, string Reason);

/// <summary>Immutable confirmation, including an empty-bin observation with no holding rows.</summary>
public sealed record BinCountOutcome(Guid TaskId, DateTimeOffset ConfirmedAt, IReadOnlyList<CountOutcome> Counts);

/// <summary>Inventory reconciles an entire physical bin without disclosing its expected contents.</summary>
public interface IBlindBinCounting
{
    Task<long> ReadVersionAsync(Guid locationId, CancellationToken cancellationToken);
    Task<BinCountOutcome?> ReadConfirmationAsync(Guid taskId, CancellationToken cancellationToken);
    Task<Result<BinCountOutcome>> ConfirmAsync(BinCountInstruction instruction, CancellationToken cancellationToken);
}
