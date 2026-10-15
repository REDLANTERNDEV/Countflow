using Countflow.Modules.Replenishment.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Replenishment.Features;

public sealed record SavePickFaceRequest(string Sku, decimal Minimum, decimal Maximum, bool Enabled = false);
public sealed record PickFacePolicyResponse(Guid LocationId, string LocationCode, string WarehouseCode, string Sku,
    string BaseUom, decimal Minimum, decimal Maximum, bool Enabled, decimal OnHandQuantity,
    decimal AvailableQuantity, decimal IncomingQuantity, DateTimeOffset? LastCheckedAt, decimal ShortfallQuantity,
    Guid? PendingPlanId, Guid? LastPlanId, Guid? LastDecisionTraceId, string? LastErrorCode);
public sealed record PickFacePageResponse(IReadOnlyList<PickFacePolicyResponse> Items, int Total, int Page, int PageSize);
public sealed record ReplenishmentLineResponse(Guid Id, string FromLocationCode, decimal Quantity,
    Guid? ReservationId, Guid? WorkTaskId, WorkTaskState? State, bool Refused, string? ErrorCode);
public sealed record ReplenishmentPlanResponse(Guid Id, string Reference, ReplenishmentPlanStatus Status,
    string LocationCode, string Sku, decimal RequestedQuantity, Guid DecisionTraceId, DateTimeOffset CreatedAt,
    IReadOnlyList<ReplenishmentLineResponse> Lines);
public sealed record CancelReplenishmentResponse(Guid Id, int CancelledTasks, int ReleasedReservations);
