using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>Work reserves the receipt-to-task handoff before Inventory books it.</summary>
public sealed record PutawayReceiptPreparation(string IdempotencyKey, string Fingerprint,
    string Sscc, Guid WarehouseId, Guid FromLocationId, Guid? ExistingLicensePlateId);

public interface IPutawayReceipts
{
    Task<Result> PrepareAsync(PutawayReceiptPreparation request, CancellationToken cancellationToken);
    /// <summary>Only for a definite posting refusal. Ambiguous failures stay pending for retry.</summary>
    Task AbortAsync(string idempotencyKey, CancellationToken cancellationToken);
}
