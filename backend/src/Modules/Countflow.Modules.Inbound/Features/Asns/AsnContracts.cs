using Countflow.Modules.Inbound.Domain;

namespace Countflow.Modules.Inbound.Features.Asns;

public sealed record AsnLineResponse(
    int LineNumber,
    string Sku,
    string Description,
    string BaseUom,
    decimal ExpectedQuantity,
    decimal ReceivedQuantity,
    decimal OutstandingQuantity,
    string? ExpectedLotNumber,
    DateOnly? ExpectedExpiryDate);

public sealed record AsnResponse(
    Guid Id,
    string Reference,
    AsnStatus Status,
    string? SupplierName,
    DateTimeOffset? ExpectedAt,
    string? CarrierReference,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<AsnLineResponse> Lines);

public sealed record AsnPage(IReadOnlyList<AsnResponse> Items, int Total, int Page, int PageSize);

public sealed record CreateAsnLineRequest(
    string Sku,
    decimal ExpectedQuantity,
    string? ExpectedLotNumber = null,
    DateOnly? ExpectedExpiryDate = null);

/// <param name="WarehouseCode">Which site the truck is coming to.</param>
public sealed record CreateAsnRequest(
    string Reference,
    string WarehouseCode,
    IReadOnlyList<CreateAsnLineRequest> Lines,
    string? SupplierName = null,
    DateTimeOffset? ExpectedAt = null,
    string? CarrierReference = null,
    string? Notes = null);
