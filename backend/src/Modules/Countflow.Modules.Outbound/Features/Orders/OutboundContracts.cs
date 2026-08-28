using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>
/// One commitment, and what became of the job that fetches it.
/// </summary>
/// <param name="TaskState">
/// Read from the work queue at request time rather than mirrored into this
/// module. A copy kept here would be right until the first pick confirmed while
/// nobody was looking.
/// </param>
/// <param name="PickedQuantity">
/// The whole allocation once its pick is confirmed, and nothing before that. A
/// pick discharges exactly the quantity that was reserved — the ledger posts it in
/// one movement inside one transaction — so there is no partial state to report.
/// </param>
public sealed record OrderAllocationResponse(
    Guid Id,
    decimal Quantity,
    string LocationCode,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid DecisionTraceId,
    Guid? WorkTaskId,
    WorkTaskState? TaskState,
    decimal PickedQuantity,
    string? PickedAtLocationCode);

public sealed record OrderLineResponse(
    Guid Id,
    int LineNumber,
    string Sku,
    string Description,
    string BaseUom,
    decimal OrderedQuantity,
    decimal AllocatedQuantity,
    decimal OutstandingQuantity,
    decimal PickedQuantity,
    decimal ReturnedQuantity,
    string? RequiredLotNumber,
    IReadOnlyList<OrderAllocationResponse> Allocations);

public sealed record SalesOrderResponse(
    Guid Id,
    string Reference,
    string? CustomerName,
    SalesOrderStatus Status,
    string? StagingLocationCode,
    DateOnly? ShipBy,
    int Priority,
    Guid? WaveId,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? PackedAt,
    DateTimeOffset? DespatchedAt,
    IReadOnlyList<OrderLineResponse> Lines);

public sealed record SalesOrderPage(IReadOnlyList<SalesOrderResponse> Items, int Total, int Page, int PageSize);

/// <param name="LotNumber">A batch the customer named. Absolute when present — see the lot-match constraint.</param>
public sealed record CreateOrderLineRequest(string Sku, decimal Quantity, string? LotNumber = null);

public sealed record CreateOrderRequest(
    string WarehouseCode,
    string Reference,
    IReadOnlyList<CreateOrderLineRequest> Lines,
    string? CustomerName = null,
    string? StagingLocationCode = null,
    DateOnly? ShipBy = null,
    int? Priority = null,
    string? Notes = null);

/// <param name="StagingLocationCode">
/// Where the picks are dropped. Optional here because it may already be on the
/// order; required by the time the release runs, since a pick with nowhere to go
/// is not a job anybody can do.
/// </param>
public sealed record ReleaseOrderRequest(string? StagingLocationCode = null);

/// <param name="ShortfallQuantity">
/// What the rules could not find stock for. Positive is not a failure — a line
/// half allocated is half picked today and the rest chased tomorrow, which is what
/// warehouses actually do.
/// </param>
public sealed record ReleaseOrderResponse(
    SalesOrderResponse Order,
    int AllocationsRaised,
    int TasksRaised,
    decimal ShortfallQuantity);

/// <summary>
/// Deliberately almost empty.
/// <para>
/// It carried a carton count for one draft, and that was a field the endpoint
/// accepted and dropped — a published surface that quietly discards what it is
/// given is worse than one that does not offer it. Cartons need a packing model
/// (a container entity, its own labels, an SSCC per carton) and that is Phase 7's
/// integration surface, not a nullable int here.
/// </para>
/// </summary>
public sealed record PackOrderRequest(string? Notes = null);

public sealed record DespatchOrderResponse(
    SalesOrderResponse Order,
    IReadOnlyList<PostedMovement> Movements);

public sealed record CancelOrderRequest(string Reason);

public sealed record CancelOrderResponse(
    SalesOrderResponse Order,
    int ReservationsReleased,
    int TasksCancelled);

public sealed record WaveResponse(
    Guid Id,
    string Reference,
    WaveStatus Status,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedByUserName,
    IReadOnlyList<SalesOrderResponse> Orders);

public sealed record CreateWaveRequest(
    string WarehouseCode,
    string Reference,
    IReadOnlyList<Guid> OrderIds,
    string? Notes = null);

public sealed record ReleaseWaveRequest(string? StagingLocationCode = null);

public sealed record ReleaseWaveResponse(
    WaveResponse Wave,
    int OrdersReleased,
    int TasksRaised,
    decimal ShortfallQuantity);
