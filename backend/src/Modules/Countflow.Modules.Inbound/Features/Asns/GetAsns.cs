using Countflow.Modules.Inbound.Domain;
using Countflow.Modules.Inbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inbound.Features.Asns;

/// <summary>What is expected, and how much of it has turned up.</summary>
public static class GetAsns
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inbound/asns", async (
                InboundDbContext dbContext,
                IItemDirectory items,
                CancellationToken cancellationToken,
                AsnStatus? status = null,
                string? reference = null,
                int page = 1,
                int pageSize = 50) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 200);
                page = Math.Max(page, 1);

                var query = dbContext.Asns.AsNoTracking();

                if (status is { } wanted) query = query.Where(a => a.Status == wanted);

                if (!string.IsNullOrWhiteSpace(reference))
                {
                    var trimmed = reference.Trim();
                    query = query.Where(a => a.Reference == trimmed);
                }

                var total = await query.CountAsync(cancellationToken);

                var rows = await query
                    // Open notes first, soonest expected at the top: the dock's
                    // question is "what is coming next", not "what happened".
                    .OrderBy(a => a.Status)
                    .ThenBy(a => a.ExpectedAt ?? a.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Include(a => a.Lines)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new AsnPage(
                    await DescribeAsync(rows, items, cancellationToken), total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Inbound")
            .WithName("GetAsns")
            .WithSummary("Advance shipping notices and their receiving progress.")
            .Produces<AsnPage>();

        endpoints.MapGet("/inbound/asns/{reference}", async (
                string reference,
                InboundDbContext dbContext,
                IItemDirectory items,
                CancellationToken cancellationToken) =>
            {
                var trimmed = reference.Trim();

                var asn = await dbContext.Asns
                    .AsNoTracking()
                    .Include(a => a.Lines)
                    .SingleOrDefaultAsync(a => a.Reference == trimmed, cancellationToken);

                if (asn is null)
                {
                    return Result.Failure<AsnResponse>(InboundErrors.AsnNotFound).ToHttpResult();
                }

                return Results.Ok((await DescribeAsync([asn], items, cancellationToken))[0]);
            })
            .RequireAuthorization()
            .WithTags("Inbound")
            .WithName("GetAsn")
            .WithSummary("One advice note, with its lines.")
            .Produces<AsnResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// One batch lookup across the module boundary rather than a join — Inbound
    /// cannot join to <c>masterdata.Items</c> (ADR 0002).
    /// </summary>
    internal static async Task<IReadOnlyList<AsnResponse>> DescribeAsync(
        IReadOnlyList<Asn> asns,
        IItemDirectory items,
        CancellationToken cancellationToken)
    {
        if (asns.Count == 0)
        {
            return [];
        }

        var itemsById = await items.FindManyAsync(
            [.. asns.SelectMany(a => a.Lines).Select(l => l.ItemId).Distinct()], cancellationToken);

        return
        [
            .. asns.Select(asn => new AsnResponse(
                asn.Id,
                asn.Reference,
                asn.Status,
                asn.SupplierName,
                asn.ExpectedAt,
                asn.CarrierReference,
                asn.Notes,
                asn.CreatedAt,
                asn.ClosedAt,
                [
                    .. asn.Lines.OrderBy(l => l.LineNumber).Select(line =>
                    {
                        var item = itemsById.GetValueOrDefault(line.ItemId);

                        return new AsnLineResponse(
                            line.LineNumber,
                            item?.Sku ?? string.Empty,
                            item?.Description ?? string.Empty,
                            item?.BaseUom ?? string.Empty,
                            line.ExpectedQuantity,
                            line.ReceivedQuantity,
                            line.OutstandingQuantity,
                            line.ExpectedLotNumber,
                            line.ExpectedExpiryDate);
                    })
                ]))
        ];
    }
}
