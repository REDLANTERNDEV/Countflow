using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <param name="Sku">What came back. Resolved against master data, not against the order — a customer can send back something that was never on it.</param>
/// <param name="Quantity">In the item's base unit. A handheld holding a case barcode converts first through <c>/api/scan/resolve</c>.</param>
/// <param name="OrderLineNumber">
/// The line it came back from, when the desk knows. Omitted, the first line on the
/// order for that SKU is used — the same match a dock receipt makes against an
/// advice note.
/// </param>
public sealed record BookReturnLineRequest(
    string Sku,
    decimal Quantity,
    int? OrderLineNumber = null,
    string? LotNumber = null,
    DateOnly? ExpiryDate = null,
    string? Sscc = null);

/// <param name="OrderReference">
/// The order it came back from. Optional: a return with nothing but a scribbled
/// note on the pallet is the exact mirror of the blind receipt the dock has always
/// accepted, and refusing it at the desk would mean the goods sit in a corner
/// unbooked.
/// </param>
/// <param name="ToLocationCode">
/// The returns bay. Must be a hold-role location — stock whose condition nobody has
/// established yet must not land anywhere it could be allocated from.
/// </param>
public sealed record BookReturnRequest(
    string WarehouseCode,
    string Reference,
    string ToLocationCode,
    IReadOnlyList<BookReturnLineRequest> Lines,
    string? OrderReference = null,
    string? CustomerName = null,
    string? Notes = null);

/// <param name="Advice">
/// What the rules said, whether or not it was followed. Always present, because the
/// rules always run.
/// </param>
/// <param name="WasOverridden">True when a person chose something else.</param>
public sealed record ReturnDispositionResponse(
    Guid Id,
    Disposition Disposition,
    decimal Quantity,
    Guid DecisionTraceId,
    bool WasOverridden,
    Guid MovementId,
    Guid? WorkTaskId,
    DateTimeOffset DecidedAt,
    string? DecidedByUserName,
    string? Notes);

public sealed record ReturnLineResponse(
    Guid Id,
    int LineNumber,
    string Sku,
    string Description,
    string BaseUom,
    decimal Quantity,
    decimal DispositionedQuantity,
    decimal OutstandingQuantity,
    string? LotNumber,
    DateOnly? ExpiryDate,
    Guid? SalesOrderLineId,
    Guid? InspectionTaskId,
    WorkTaskState? InspectionState,
    IReadOnlyList<ReturnDispositionResponse> Dispositions);

public sealed record ReturnResponse(
    Guid Id,
    string Reference,
    ReturnStatus Status,
    string? SalesOrderReference,
    Guid? SalesOrderId,
    string? CustomerName,
    string ReceivedAtLocationCode,
    DateTimeOffset CreatedAt,
    string? CreatedByUserName,
    DateTimeOffset? ClosedAt,
    string? Notes,
    IReadOnlyList<ReturnLineResponse> Lines);

public sealed record ReturnPage(IReadOnlyList<ReturnResponse> Items, int Total, int Page, int PageSize);

public sealed record BookReturnResponse(
    ReturnResponse Return,
    IReadOnlyList<PostedMovement> Movements,
    int InspectionsRaised);

/// <summary>
/// What the inspector found, and what they want done about it.
/// </summary>
/// <param name="Disposition">
/// The override. Omitted — which is the ordinary case — whatever the rules rank
/// first is carried out. Supplied, the rules still run and still write their trace,
/// and the disagreement is recorded next to what was disagreed with; a system that
/// skipped the evaluation on an override would lose the only record of what it
/// would have done.
/// </param>
/// <param name="Quantity">
/// How much of the line this decision covers. Omitted, all of what is still
/// outstanding — four back on the shelf and two scrapped is an ordinary outcome,
/// and it is two calls.
/// </param>
/// <param name="Notes">
/// Required for a scrap, and passed to <c>ScrapInstruction.Notes</c>, which is not
/// optional: every other movement is explained by its document and a write-off is
/// explained only by the person who ordered it.
/// </param>
public sealed record DispositionReturnLineRequest(
    StockCondition Condition,
    bool IsSealed = false,
    bool IsRepairable = false,
    Disposition? Disposition = null,
    decimal? Quantity = null,
    string? Notes = null);

/// <param name="AdvisedDisposition">
/// What the rules ranked first. Null when the rule set found nothing admissible,
/// which is a real answer and the reason the trace id beside it is not nullable.
/// </param>
/// <param name="SuggestedLocationCode">
/// Where the putaway raised by a restock is headed. Null for the other three, and
/// null for a restock the putaway rules could not place — which still raises the
/// task, exactly as a dock receipt does.
/// </param>
public sealed record DispositionReturnLineResponse(
    ReturnResponse Return,
    Disposition Applied,
    Disposition? AdvisedDisposition,
    bool WasOverridden,
    Guid DecisionTraceId,
    IReadOnlyList<ScoreLine> Reasons,
    int ConsideredCount,
    int AdmissibleCount,
    PostedMovement Movement,
    Guid? WorkTaskId,
    string? SuggestedLocationCode);
