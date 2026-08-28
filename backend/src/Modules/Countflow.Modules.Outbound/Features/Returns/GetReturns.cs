using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <summary>
/// The returns book, newest first.
/// <para>
/// Filtered on status by default to <see cref="ReturnStatus.Received"/> when the
/// caller asks for it, because the useful question at a returns desk is "what is
/// still standing in the bay waiting for somebody" rather than "what has ever come
/// back".
/// </para>
/// </summary>
public static class GetReturns
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/outbound/returns", async (
                OutboundDbContext dbContext,
                ReturnDescriber describe,
                ReturnStatus? status,
                int? page,
                int? pageSize,
                CancellationToken cancellationToken) =>
                    Results.Ok(await HandleAsync(
                        dbContext, describe, status, page, pageSize, cancellationToken)))
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("GetReturns")
            .WithSummary("List customer returns and what has been decided about them.")
            .Produces<ReturnPage>();
    }

    private static async Task<ReturnPage> HandleAsync(
        OutboundDbContext dbContext,
        ReturnDescriber describe,
        ReturnStatus? status,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);

        var query = dbContext.Returns.AsNoTracking();

        if (status is { } wanted)
        {
            query = query.Where(r => r.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);

        var returns = await query
            .Include(r => r.Lines)
            .ThenInclude(l => l.Dispositions)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return new ReturnPage(
            await describe.DescribeAsync(returns, cancellationToken), total, number, size);
    }
}

/// <summary>One return, with every line and every decision made about it.</summary>
public static class GetReturn
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/outbound/returns/{id:guid}", async (
                Guid id,
                OutboundDbContext dbContext,
                ReturnDescriber describe,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(id, dbContext, describe, cancellationToken)).ToHttpResult())
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("GetReturn")
            .WithSummary("Read one return.")
            .Produces<ReturnResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<ReturnResponse>> HandleAsync(
        Guid id,
        OutboundDbContext dbContext,
        ReturnDescriber describe,
        CancellationToken cancellationToken)
    {
        var returned = await dbContext.Returns
            .AsNoTracking()
            .Include(r => r.Lines)
            .ThenInclude(l => l.Dispositions)
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        return returned is null
            ? Result.Failure<ReturnResponse>(OutboundErrors.ReturnNotFound)
            : await describe.DescribeAsync(returned, cancellationToken);
    }
}
