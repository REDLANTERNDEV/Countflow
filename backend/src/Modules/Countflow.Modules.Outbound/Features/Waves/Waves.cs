using System.Security.Claims;
using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Features.Orders;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Features.Waves;

public static class GetWaves
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/outbound/waves", async (
                OutboundDbContext dbContext,
                OrderDescriber describe,
                CancellationToken cancellationToken,
                WaveStatus? status = null) =>
            {
                var waves = await dbContext.Waves
                    .AsNoTracking()
                    .Where(w => status == null || w.Status == status)
                    .OrderByDescending(w => w.CreatedAt)
                    .Take(100)
                    .ToListAsync(cancellationToken);

                var waveIds = waves.Select(w => w.Id).ToList();

                var orders = await dbContext.SalesOrders
                    .AsNoTracking()
                    .Include(o => o.Lines)
                    .ThenInclude(l => l.Allocations)
                    .Where(o => o.WaveId != null && waveIds.Contains(o.WaveId!.Value))
                    .ToListAsync(cancellationToken);

                var described = await describe.DescribeAsync(orders, cancellationToken);
                var byWave = orders
                    .Select((order, index) => (order.WaveId, Response: described[index]))
                    .GroupBy(row => row.WaveId!.Value)
                    .ToDictionary(group => group.Key, group => group.Select(row => row.Response).ToList());

                return Results.Ok(waves
                    .Select(wave => new WaveResponse(
                        wave.Id,
                        wave.Reference,
                        wave.Status,
                        wave.Notes,
                        wave.CreatedAt,
                        wave.ReleasedAt,
                        wave.ReleasedByUserName,
                        byWave.GetValueOrDefault(wave.Id) ?? []))
                    .ToList());
            })
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("GetWaves")
            .WithSummary("Waves and the orders grouped into them.")
            .Produces<IReadOnlyList<WaveResponse>>();
    }
}

/// <summary>
/// Groups orders to be released together.
/// <para>
/// The grouping is a person's, not a rule set's. <c>DecisionKind.Waving</c> names
/// the rules that would choose these groups — by carrier cut-off, by pick density,
/// by zone — and deliberately has no vocabulary yet: the thing that makes a waving
/// rule worth having is being able to replay it against a month of real orders and
/// see whether it saved anybody a walk, and that is Phase 6's machinery.
/// </para>
/// </summary>
public static class CreateWave
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outbound/waves", async (
                CreateWaveRequest request,
                OutboundDbContext dbContext,
                OrderDescriber describe,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, dbContext, describe, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("CreateWave")
            .WithSummary("Group orders for release together.")
            .Produces<WaveResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<WaveResponse>> HandleAsync(
        CreateWaveRequest request,
        OutboundDbContext dbContext,
        OrderDescriber describe,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        if (request.OrderIds.Count == 0)
        {
            return Result.Failure<WaveResponse>(OutboundErrors.NoOrders);
        }

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<WaveResponse>(OutboundErrors.WarehouseNotFound);

        var reference = request.Reference.Trim();

        if (await dbContext.Waves.AnyAsync(
                w => w.WarehouseId == warehouse.Id && w.Reference == reference, cancellationToken))
        {
            return Result.Failure<WaveResponse>(OutboundErrors.WaveReferenceTaken);
        }

        var ids = request.OrderIds.Distinct().ToList();

        var orders = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .Where(o => ids.Contains(o.Id))
            .ToListAsync(cancellationToken);

        if (orders.Count != ids.Count)
        {
            return Result.Failure<WaveResponse>(OutboundErrors.OrderNotFound);
        }

        // Only untouched orders. An order already released has its stock committed
        // and its picks in the queue, and sweeping it into a wave would promise a
        // second release of work that is already being done.
        if (orders.FirstOrDefault(o => o.Status != SalesOrderStatus.New || o.WaveId is not null) is { } blocked)
        {
            return Result.Failure<WaveResponse>(OutboundErrors.OrderNotWaveable(blocked.Reference));
        }

        var wave = new Wave
        {
            WarehouseId = warehouse.Id,
            Reference = reference,
            Notes = request.Notes
        };

        dbContext.Waves.Add(wave);

        foreach (var order in orders)
        {
            order.WaveId = wave.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new WaveResponse(
            wave.Id,
            wave.Reference,
            wave.Status,
            wave.Notes,
            wave.CreatedAt,
            wave.ReleasedAt,
            wave.ReleasedByUserName,
            await describe.DescribeAsync(orders, cancellationToken));
    }
}

/// <summary>
/// Releases every order in a wave, through the same path a single order takes.
/// <para>
/// One order failing does not take the wave down with it. A wave of forty orders
/// where one names a SKU with nothing allocatable should release the other
/// thirty-nine — the shortfall is reported and the rest of the shift's work goes
/// into the queue, which is the whole reason waves exist.
/// </para>
/// </summary>
public static class ReleaseWave
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outbound/waves/{id:guid}/release", async (
                Guid id,
                ReleaseWaveRequest? request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                OutboundDbContext dbContext,
                OrderRelease release,
                OrderDescriber describe,
                ILocationDirectory locations,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, request, user, idempotencyKey, dbContext, release, describe, locations, clock,
                        cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Outbound")
            .WithName("ReleaseWave")
            .WithSummary("Allocate and raise the picks for every order in a wave.")
            .Produces<ReleaseWaveResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<ReleaseWaveResponse>> HandleAsync(
        Guid id,
        ReleaseWaveRequest? request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        OutboundDbContext dbContext,
        OrderRelease release,
        OrderDescriber describe,
        ILocationDirectory locations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReleaseWaveResponse>(OutboundErrors.IdempotencyKeyRequired);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<ReleaseWaveResponse>(actor.Error);

        var wave = await dbContext.Waves.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (wave is null) return Result.Failure<ReleaseWaveResponse>(OutboundErrors.WaveNotFound);

        if (wave.Status == WaveStatus.Cancelled)
        {
            return Result.Failure<ReleaseWaveResponse>(OutboundErrors.WaveNotReleasable);
        }

        Guid? staging = null;
        if (!string.IsNullOrWhiteSpace(request?.StagingLocationCode))
        {
            var resolved = await CreateOrder.ResolveStagingAsync(
                locations, request.StagingLocationCode, cancellationToken);

            if (resolved.IsFailure) return Result.Failure<ReleaseWaveResponse>(resolved.Error);

            staging = resolved.Value;
        }

        var orders = await dbContext.SalesOrders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Allocations)
            .Where(o => o.WaveId == wave.Id)
            .OrderBy(o => o.Priority)
            .ThenBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        var releasable = orders
            .Where(o => o.Status is SalesOrderStatus.New or SalesOrderStatus.Released)
            .ToList();

        var released = 0;
        var tasks = 0;
        var shortfall = 0m;

        foreach (var order in releasable)
        {
            if (staging is { } bench)
            {
                order.StagingLocationId = bench;
            }

            // Each order's key is derived from the wave's, so the whole wave can be
            // retried and each order recognises its own earlier attempt.
            var outcome = await release.ReleaseAsync(
                order, $"{idempotencyKey}:{order.Id}", actor.Value, cancellationToken);

            if (outcome.IsFailure)
            {
                // Recorded on the order rather than thrown at the wave. Thirty-nine
                // orders' worth of work is not held hostage by the fortieth, and
                // the one that failed is visible as still New with its reason in
                // the log.
                order.Notes = $"Release failed: {outcome.Error.Code}";
                continue;
            }

            released++;
            tasks += outcome.Value.Tasks;
            shortfall += outcome.Value.Shortfall;
        }

        wave.Status = WaveStatus.Released;
        wave.ReleasedAt ??= clock.GetUtcNow();
        wave.ReleasedByUserName ??= actor.Value.UserName;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ReleaseWaveResponse(
            new WaveResponse(
                wave.Id,
                wave.Reference,
                wave.Status,
                wave.Notes,
                wave.CreatedAt,
                wave.ReleasedAt,
                wave.ReleasedByUserName,
                await describe.DescribeAsync(orders, cancellationToken)),
            released,
            tasks,
            shortfall);
    }
}
