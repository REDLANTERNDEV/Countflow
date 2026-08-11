using Countflow.Modules.Inbound.Domain;
using Countflow.Modules.Inbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inbound.Features.Asns;

/// <summary>
/// Announces a delivery.
/// <para>
/// Supervisor-and-above, because an advice note is what receipts are checked
/// against: anyone who can invent one can make a short delivery reconcile. In a
/// live installation most notes arrive over EDI rather than through this
/// endpoint, which exists for the cases EDI never covers — a supplier who phones,
/// and a demo.
/// </para>
/// </summary>
public static class CreateAsn
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inbound/asns", async (
                CreateAsnRequest request,
                InboundDbContext dbContext,
                IItemDirectory items,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, dbContext, items, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inbound")
            .WithName("CreateAsn")
            .WithSummary("Announce an inbound delivery.")
            .Produces<AsnResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<AsnResponse>> HandleAsync(
        CreateAsnRequest request,
        InboundDbContext dbContext,
        IItemDirectory items,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        if (request.Lines.Count == 0)
        {
            return Result.Failure<AsnResponse>(InboundErrors.NoLines);
        }

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<AsnResponse>(InboundErrors.LocationNotFound);

        var reference = request.Reference.Trim();

        if (await dbContext.Asns.AnyAsync(
                a => a.WarehouseId == warehouse.Id && a.Reference == reference, cancellationToken))
        {
            return Result.Failure<AsnResponse>(InboundErrors.AsnReferenceTaken);
        }

        var asn = new Asn
        {
            WarehouseId = warehouse.Id,
            Reference = reference,
            SupplierName = request.SupplierName,
            ExpectedAt = request.ExpectedAt,
            CarrierReference = request.CarrierReference,
            Notes = request.Notes
        };

        var lineNumber = 1;

        foreach (var line in request.Lines)
        {
            if (line.ExpectedQuantity <= 0m)
            {
                return Result.Failure<AsnResponse>(InboundErrors.QuantityNotPositive);
            }

            var item = await items.FindBySkuAsync(line.Sku.Trim(), cancellationToken);
            if (item is null) return Result.Failure<AsnResponse>(InboundErrors.ItemNotFound);

            asn.Lines.Add(new AsnLine
            {
                AsnId = asn.Id,
                LineNumber = lineNumber++,
                ItemId = item.Id,
                ExpectedQuantity = line.ExpectedQuantity,
                ExpectedLotNumber = line.ExpectedLotNumber,
                ExpectedExpiryDate = line.ExpectedExpiryDate
            });
        }

        dbContext.Asns.Add(asn);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (await GetAsns.DescribeAsync([asn], items, cancellationToken))[0];
    }
}
