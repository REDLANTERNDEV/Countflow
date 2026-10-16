using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// An explicit whole-load question. The orchestrator supplies the exact contents
/// it read at the origin; the advisor records that intent, never a fresh read of
/// a growing pallet. The caller owns freshness and task revision coordination.
/// </summary>
public sealed record PalletPutawayQuery
{
    public required Guid WarehouseId { get; init; }
    public required Guid LicensePlateId { get; init; }
    public required Guid FromLocationId { get; init; }
    public required IReadOnlyList<PlateLine> Contents { get; init; }
    public required PostingActor Actor { get; init; }
}

public interface IPalletPutawayAdvisor
{
    Task<Result<PutawayAdvice>> SuggestAsync(PalletPutawayQuery query, CancellationToken cancellationToken);
}
