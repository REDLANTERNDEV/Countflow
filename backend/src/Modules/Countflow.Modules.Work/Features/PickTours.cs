using System.Security.Claims;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Countflow.Modules.Work.Features;

/// <param name="MaxStops">
/// How many picks the operator is willing to carry. Clamped rather than validated:
/// a scanner sending nonsense should get a sensible tour, not a refusal in front of
/// somebody holding a trolley.
/// </param>
public sealed record ClaimNextTourRequest(Guid? WarehouseId = null, int MaxStops = 12);

public sealed record PickTourSegmentResponse(
    string? ZoneCode,
    string RequestedStrategy,
    string AppliedStrategy,
    string? FellBackBecause,
    int Stops);

/// <param name="StepsSaved">
/// Against working the same stops in the order the queue would have handed them out.
/// Null when the geometry could not measure one of the two walks, and <b>negative
/// when the strategy cost more than doing nothing</b> — which is not clamped, because
/// that is the finding, and a zero there is how a bad setting survives for years.
/// </param>
/// <param name="Stops">The walk, in order.</param>
public sealed record PickTourResponse(
    Guid Id,
    Guid WarehouseId,
    string AssignedToUserName,
    DateTimeOffset ClaimedAt,
    int StopCount,
    int Remaining,
    bool IsComplete,
    int? TravelSteps,
    int? BaselineTravelSteps,
    int? StepsSaved,
    int? AisleChanges,
    DateTimeOffset? ReleasedAt,
    int ReleasedStops,
    IReadOnlyList<PickTourSegmentResponse> Segments,
    IReadOnlyList<WorkTaskResponse> Stops);

public sealed record PickTourPage(IReadOnlyList<PickTourResponse> Items, int Total, int Page, int PageSize);

/// <summary>
/// Hands an operator a set of picks and the order to walk them in.
/// <para>
/// <b>Claiming a set, not a task, and that is forced.</b> A picker who takes stop one
/// of a sequence, walks to it, presses "next job" and is handed stop one of somebody
/// else's sequence has gained nothing — so a sequence has to be claimed whole or it is
/// decoration. Everything else here follows from that: the picks are locked first and
/// ordered second, so the walk is a sequence over its own membership rather than a
/// pre-computed list two operators can take holes out of. See <see cref="PickTour"/>.
/// </para>
/// <para>
/// <b>Read, then lock, then sequence.</b> Route position lives in MasterData and the
/// queue lives here, and no module joins to another's tables (ADR 0002) — so this
/// cannot be one statement the way <c>claim-next</c> is. It reads a pool without
/// locking, chooses from it, locks what it chose with <c>FOR UPDATE SKIP LOCKED</c>,
/// and then sequences <i>what came back</i>. Losing some of the pool to a rival is
/// therefore ordinary rather than a race to defend against: the tour is simply
/// smaller, and it is correctly ordered for the stops it has.
/// </para>
/// <para>
/// <b>Still one server decision, so the offline rule is untouched.</b> ADR 0008 says
/// a device may work what it has already been given and may never help itself to
/// more; this is that same request with a larger answer. The offline story gets
/// better rather than worse — a handheld that used to hold one job out of signal now
/// holds twelve, in order.
/// </para>
/// </summary>
public static class ClaimNextTour
{
    /// <summary>
    /// How much of the queue is looked at before choosing. Wide enough that the stops
    /// around the seed are really the nearest ones and not merely the first ones
    /// raised, and bounded because this runs every time thirty people press a button
    /// at the start of a shift.
    /// </summary>
    private const int PoolMultiplier = 8;

    private const int PoolCeiling = 200;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tours/claim-next", async (
                ClaimNextTourRequest? request,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ClaimsPrincipal user,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                ILocationDirectory locations,
                TimeProvider clock,
                ILogger<PickTour> logger,
                CancellationToken cancellationToken) =>
                    (await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(() => HandleAsync(
                        request, idempotencyKey, user, dbContext, describe, locations, clock, logger, cancellationToken)))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("ClaimNextTour")
            .WithSummary("Take the next run of picks, sequenced into a walk.")
            .Produces<PickTourResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<PickTourResponse>> HandleAsync(
        ClaimNextTourRequest? request,
        string? idempotencyKey,
        ClaimsPrincipal user,
        WorkDbContext dbContext,
        WorkTaskDescriber describe,
        ILocationDirectory locations,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<PickTourResponse>(actor.Error);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            return Result.Failure<PickTourResponse>(WorkErrors.IdempotencyKeyRequired);

        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Serialize retries of one intent, including requests arriving before the
        // first response. Other operators and other keys remain independent.
        var intent = $"tour:{actor.Value.UserId}:{idempotencyKey}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({intent}, 0))", cancellationToken);
        var previous = await dbContext.PickTours.Include(t => t.Segments)
            .SingleOrDefaultAsync(t => t.AssignedToUserId == actor.Value.UserId
                                      && t.ClaimIdempotencyKey == idempotencyKey, cancellationToken);
        if (previous is not null)
        {
            var previousStops = await PickTourReader.StopsAsync(dbContext, [previous.Id], cancellationToken);
            return await PickTourReader.DescribeAsync(previous,
                previousStops.GetValueOrDefault(previous.Id) ?? [], describe, cancellationToken);
        }

        var maxStops = Math.Clamp(request?.MaxStops ?? 12, 1, 50);
        var warehouseId = request?.WarehouseId;

        var pool = await dbContext.WorkTasks
            .AsNoTracking()
            .Where(t => t.Status == WorkTaskStatus.Ready
                        && t.Kind == WorkTaskKind.Pick
                        && (warehouseId == null || t.WarehouseId == warehouseId))
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Take(Math.Min(maxStops * PoolMultiplier, PoolCeiling))
            .Select(t => new PoolEntry(t.Id, t.WarehouseId, t.Priority, t.FromLocationId))
            .ToListAsync(cancellationToken);

        if (pool.Count == 0)
        {
            return Result.Failure<PickTourResponse>(WorkErrors.NoWorkAvailable);
        }

        var map = await locations.FindPickRouteMapAsync(
            [.. pool.Select(entry => entry.FromLocationId).Distinct()], cancellationToken);

        var wanted = Choose(pool, map, maxStops);

        var claimed = await LockAsync(dbContext, actor.Value, wanted, clock.GetUtcNow(), cancellationToken);

        if (claimed.Count == 0)
        {
            // Every one of them went to somebody else between reading the pool and
            // locking it. That is a busy warehouse rather than an error, and it reads
            // to the operator exactly as an empty queue does.
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<PickTourResponse>(WorkErrors.NoWorkAvailable);
        }

        var tasks = await dbContext.WorkTasks
            .Where(t => claimed.Contains(t.Id))
            .ToListAsync(cancellationToken);

        // The order the queue would have handed these out in, which is what makes the
        // baseline figure mean anything.
        var baseline = tasks
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToList();

        var plan = map.Plan(baseline.Select(t => t.FromLocationId));
        var walk = Reorder(baseline, plan);

        var tour = new PickTour
        {
            WarehouseId = baseline[0].WarehouseId,
            AssignedToUserId = actor.Value.UserId,
            AssignedToUserName = actor.Value.UserName,
            ClaimedAt = clock.GetUtcNow(),
            ClaimIdempotencyKey = idempotencyKey,
            StopCount = walk.Count,
            TravelSteps = plan.Travel.IsMeasured ? plan.Travel.Steps : null,
            BaselineTravelSteps = plan.Baseline.IsMeasured ? plan.Baseline.Steps : null,
            AisleChanges = plan.Travel.IsMeasured ? plan.Travel.AisleChanges : null
        };

        dbContext.PickTours.Add(tour);

        foreach (var segment in plan.Segments)
        {
            dbContext.PickTourSegments.Add(new PickTourSegment
            {
                PickTourId = tour.Id,
                ZoneCode = segment.ZoneCode,
                RequestedStrategy = segment.RequestedStrategy,
                AppliedStrategy = segment.AppliedStrategy,
                FellBackBecause = segment.FellBackBecause,
                Stops = segment.Stops
            });
        }

        for (var position = 0; position < walk.Count; position++)
        {
            walk[position].PickTourId = tour.Id;
            walk[position].TourSequence = position + 1;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var fallback in plan.Fallbacks)
        {
            logger.LogWarning(
                "Zone {Zone} is set to {Requested} but was walked with {Applied}: {Because}",
                fallback.ZoneCode,
                fallback.RequestedStrategy,
                fallback.AppliedStrategy,
                fallback.FellBackBecause);
        }

        logger.LogInformation(
            "Tour {Tour} claimed by {User}: {Stops} stops, {Steps} steps against a baseline of {Baseline}",
            tour.Id,
            actor.Value.UserName,
            tour.StopCount,
            tour.TravelSteps,
            tour.BaselineTravelSteps);

        return await PickTourReader.DescribeAsync(tour, walk, describe, cancellationToken);
    }

    private sealed record PoolEntry(Guid Id, Guid WarehouseId, int Priority, Guid FromLocationId);

    /// <summary>
    /// Picks the stops that will make up the tour: the most urgent job waiting, and
    /// then its nearest neighbours.
    /// <para>
    /// <b>Seeded rather than skimmed, and the difference matters.</b> Taking the first
    /// <c>n</c> of the queue in route order always sends every picker to the front of
    /// the building, whatever the work is doing; seeding on the job that actually has
    /// to be done next and gathering what is near it puts the tour where the urgency
    /// is. It is the shape <c>decision 11</c> already chose for waving — seed, fill,
    /// seed the next one — and it makes the same trade: greedy, not optimal, and
    /// declined on the merits rather than deferred.
    /// </para>
    /// <para>
    /// Confined to the seed's priority band, so sequencing can never quietly demote
    /// urgent work. A tour of near neighbours that skipped a high-priority pick two
    /// aisles over would be routing overruling priority, which is a decision nobody
    /// asked routing to make.
    /// </para>
    /// <para>
    /// Nearness is <see cref="RouteScale"/>, not arithmetic written out again here.
    /// That type exists because <c>zone × 1,000,000 + position</c> has been got wrong
    /// five times, every one of them by somebody re-deriving it a few lines from where
    /// it already lived.
    /// </para>
    /// </summary>
    private static List<Guid> Choose(List<PoolEntry> pool, PickRouteMap map, int maxStops)
    {
        var seed = pool[0];

        var band = pool
            .Where(entry => entry.Priority == seed.Priority && entry.WarehouseId == seed.WarehouseId)
            .ToList();

        var scale = RouteScale.Of(
            band.Select(entry => map.Stops.GetValueOrDefault(entry.FromLocationId)).OfType<RouteStop>());

        var origin = map.Stops.TryGetValue(seed.FromLocationId, out var seedStop)
            ? scale.Normalise(seedStop)
            : 0m;

        return
        [
            .. band
                .OrderBy(entry => entry.Id == seed.Id ? 0 : 1)
                .ThenBy(entry => Distance(entry))
                .ThenBy(entry => entry.Id)
                .Take(maxStops)
                .Select(entry => entry.Id)
        ];

        // A stop the map cannot place sorts last rather than nearest. It is a bin in
        // no zone, or a location that is not a place at all, and guessing it is next
        // to the seed would build the tour around it.
        decimal Distance(PoolEntry entry) =>
            map.Stops.TryGetValue(entry.FromLocationId, out var stop)
                ? Math.Abs(scale.Normalise(stop) - origin)
                : decimal.MaxValue;
    }

    /// <summary>
    /// Takes the tasks, in the order the queue would have given them out, and puts
    /// them in the order the plan says to walk them.
    /// <para>
    /// A queue per location rather than a lookup, because two order lines in one wave
    /// routinely draw on the same bin and the plan sees one stop where the queue has
    /// two jobs. Popping keeps them adjacent and keeps their relative order, which is
    /// the order they were raised in — an operator standing at a bin doing two picks
    /// wants them in document order, and the walk is identical either way.
    /// </para>
    /// <para>
    /// Anything the plan could not place is appended rather than dropped. A pick list
    /// that quietly loses a line is an order that quietly ships short.
    /// </para>
    /// </summary>
    private static List<WorkTask> Reorder(List<WorkTask> baseline, PickRoutePlan plan)
    {
        var byLocation = new Dictionary<Guid, Queue<WorkTask>>();

        foreach (var task in baseline)
        {
            if (!byLocation.TryGetValue(task.FromLocationId, out var queue))
            {
                queue = new Queue<WorkTask>();
                byLocation[task.FromLocationId] = queue;
            }

            queue.Enqueue(task);
        }

        var walk = new List<WorkTask>(baseline.Count);

        foreach (var stop in plan.Stops)
        {
            if (byLocation.TryGetValue(stop.LocationId, out var queue) && queue.Count > 0)
            {
                walk.Add(queue.Dequeue());
            }
        }

        var placed = walk.Select(t => t.Id).ToHashSet();
        walk.AddRange(baseline.Where(task => !placed.Contains(task.Id)));

        return walk;
    }

    private static async Task<HashSet<Guid>> LockAsync(
        WorkDbContext dbContext,
        PostingActor actor,
        List<Guid> wanted,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The same statement claim-next uses, with a set instead of a limit. SKIP
        // LOCKED is what lets thirty operators claim thirty disjoint tours at the
        // start of a shift with no retry loop; what comes back is what was actually
        // taken, which is why the sequencing happens after this and not before.
        var sql = $"""
            UPDATE {WorkDbContext.SchemaName}."WorkTasks"
            SET "Status" = @assigned,
                "AssignedToUserId" = @userId,
                "AssignedToUserName" = @userName,
                "AssignedAt" = @now
            WHERE "Id" IN (
                SELECT "Id"
                FROM {WorkDbContext.SchemaName}."WorkTasks"
                WHERE "Id" = ANY(@ids)
                  AND "Status" = @ready
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id" AS "Value"
            """;

        var claimed = await dbContext.Database
            .SqlQueryRaw<Guid>(
                sql,
                new NpgsqlParameter("assigned", NpgsqlDbType.Integer) { Value = (int)WorkTaskStatus.Assigned },
                new NpgsqlParameter("ready", NpgsqlDbType.Integer) { Value = (int)WorkTaskStatus.Ready },
                new NpgsqlParameter("userId", NpgsqlDbType.Uuid) { Value = actor.UserId },
                new NpgsqlParameter("userName", NpgsqlDbType.Text) { Value = actor.UserName },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = wanted.ToArray() })
            .ToListAsync(cancellationToken);

        return [.. claimed];
    }
}

/// <summary>
/// One tour and the walk it is, for the handheld holding it and the console watching
/// it.
/// </summary>
public static class GetPickTour
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/work/tours/{id:guid}", async (
                Guid id,
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                CancellationToken cancellationToken) =>
            {
                var tour = await dbContext.PickTours
                    .AsNoTracking()
                    .Include(t => t.Segments)
                    .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

                if (tour is null)
                {
                    return Result.Failure<PickTourResponse>(WorkErrors.TourNotFound).ToHttpResult();
                }

                var stops = await PickTourReader.StopsAsync(dbContext, [tour.Id], cancellationToken);

                return (await PickTourReader.DescribeAsync(
                        tour, stops.GetValueOrDefault(tour.Id) ?? [], describe, cancellationToken))
                    .ToHttpResult();
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("GetPickTour")
            .WithSummary("One tour, its stops in walking order, and what sequenced them.")
            .Produces<PickTourResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

/// <summary>Tours, for a supervisor watching the floor and for an operator finding their way back to one.</summary>
public static class GetPickTours
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/work/tours", async (
                WorkDbContext dbContext,
                WorkTaskDescriber describe,
                ClaimsPrincipal user,
                CancellationToken cancellationToken,
                bool mine = false,
                bool? open = null,
                int page = 1,
                int pageSize = 20) =>
            {
                pageSize = Math.Clamp(pageSize, 1, 100);
                page = Math.Max(page, 1);

                var query = dbContext.PickTours.AsNoTracking().Include(t => t.Segments).AsQueryable();

                if (mine)
                {
                    var actor = PostingActor.From(user);
                    if (actor.IsFailure)
                    {
                        return Result.Failure<PickTourPage>(actor.Error).ToHttpResult();
                    }

                    query = query.Where(t => t.AssignedToUserId == actor.Value.UserId);
                }

                if (open is { } wantOpen)
                {
                    // Open means somebody still has walking to do on it. Derived from
                    // the stops rather than stored, because a second stored answer to
                    // "is this finished" is a second answer to drift.
                    query = wantOpen
                        ? query.Where(t => t.Tasks.Any(x =>
                            x.Status == WorkTaskStatus.Ready || x.Status == WorkTaskStatus.Assigned))
                        : query.Where(t => !t.Tasks.Any(x =>
                            x.Status == WorkTaskStatus.Ready || x.Status == WorkTaskStatus.Assigned));
                }

                var total = await query.CountAsync(cancellationToken);

                var tours = await query
                    .OrderByDescending(t => t.ClaimedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);

                var stops = await PickTourReader.StopsAsync(
                    dbContext, [.. tours.Select(t => t.Id)], cancellationToken);

                var described = new List<PickTourResponse>(tours.Count);

                foreach (var tour in tours)
                {
                    var response = await PickTourReader.DescribeAsync(
                        tour, stops.GetValueOrDefault(tour.Id) ?? [], describe, cancellationToken);

                    described.Add(response.Value);
                }

                return Results.Ok(new PickTourPage(described, total, page, pageSize));
            })
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("GetPickTours")
            .WithSummary("Pick tours, newest first.")
            .Produces<PickTourPage>();
    }
}

/// <summary>
/// Hands back everything on a tour the operator has not walked yet.
/// <para>
/// <b>The cost of claiming a set, paid.</b> Handing back one job was cheap because
/// one job was all anybody could hold; an operator who claims twelve stops and goes
/// to lunch strands twelve, and releasing them one at a time on a scanner is how they
/// end up not released at all. Completed stops stay on the tour, because the tour is
/// the record of a walk that happened; the rest lose their place on it and go back to
/// the queue, so nothing is ever a stop on a walk nobody is on.
/// </para>
/// <para>
/// A tour still has to be released by a person. Nothing expires a claim in this
/// system — that needs something running on a timer, which does not exist yet — so an
/// operator whose battery dies still strands their remaining stops until a supervisor
/// releases them. See <c>ROADMAP.md</c>.
/// </para>
/// </summary>
public static class ReleasePickTour
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/work/tours/{id:guid}/release", async (
                Guid id,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ClaimsPrincipal user,
                WorkDbContext dbContext,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
                await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var actor = PostingActor.From(user);
                if (actor.IsFailure) return actor.ToHttpResult();

                if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
                    return Result.Failure(WorkErrors.IdempotencyKeyRequired).ToHttpResult();
                dbContext.ChangeTracker.Clear();
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var tour = (await dbContext.PickTours.FromSqlInterpolated(
                    $"SELECT * FROM work.\"PickTours\" WHERE \"Id\" = {id} FOR UPDATE")
                    .ToListAsync(cancellationToken)).SingleOrDefault();

                if (tour is null) return Result.Failure(WorkErrors.TourNotFound).ToHttpResult();

                if (tour.AssignedToUserId != actor.Value.UserId)
                {
                    return Result.Failure(WorkErrors.TourNotYours).ToHttpResult();
                }

                if (tour.ReleaseIdempotencyKey == idempotencyKey)
                    return Result.Success().ToHttpResult();
                var open = await dbContext.WorkTasks.FromSqlInterpolated(
                    $"SELECT * FROM work.\"WorkTasks\" WHERE \"PickTourId\" = {id} AND \"Status\" IN ({(int)WorkTaskStatus.Ready}, {(int)WorkTaskStatus.Assigned}) ORDER BY \"Id\" FOR UPDATE")
                    .ToListAsync(cancellationToken);

                if (open.Count == 0)
                {
                    return Result.Failure(WorkErrors.TourNothingToRelease).ToHttpResult();
                }

                foreach (var task in open)
                {
                    task.Status = WorkTaskStatus.Ready;
                    task.AssignedToUserId = null;
                    task.AssignedToUserName = null;
                    task.AssignedAt = null;
                    task.PickTourId = null;
                    task.TourSequence = null;
                }

                tour.ReleaseIdempotencyKey = idempotencyKey;
                tour.ReleasedAt = clock.GetUtcNow();
                tour.ReleasedStops = open.Count;

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return Result.Success().ToHttpResult();
            }))
            .RequireAuthorization()
            .WithTags("Work")
            .WithName("ReleasePickTour")
            .WithSummary("Hand back every stop on a tour that has not been walked.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>Shared shaping, so the three tour endpoints cannot disagree about what a tour looks like.</summary>
internal static class PickTourReader
{
    internal static async Task<Dictionary<Guid, List<WorkTask>>> StopsAsync(
        WorkDbContext dbContext,
        IReadOnlyCollection<Guid> tourIds,
        CancellationToken cancellationToken)
    {
        if (tourIds.Count == 0)
        {
            return [];
        }

        var rows = await dbContext.WorkTasks
            .AsNoTracking()
            .Where(t => t.PickTourId != null && tourIds.Contains(t.PickTourId!.Value))
            .OrderBy(t => t.TourSequence)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(t => t.PickTourId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    internal static async Task<Result<PickTourResponse>> DescribeAsync(
        PickTour tour,
        IReadOnlyList<WorkTask> stops,
        WorkTaskDescriber describe,
        CancellationToken cancellationToken)
    {
        var remaining = stops.Count(t => t.Status is WorkTaskStatus.Ready or WorkTaskStatus.Assigned);

        return new PickTourResponse(
            tour.Id,
            tour.WarehouseId,
            tour.AssignedToUserName,
            tour.ClaimedAt,
            tour.StopCount,
            remaining,
            remaining == 0,
            tour.TravelSteps,
            tour.BaselineTravelSteps,
            tour.TravelSteps is { } walked && tour.BaselineTravelSteps is { } baseline
                ? baseline - walked
                : null,
            tour.AisleChanges,
            tour.ReleasedAt,
            tour.ReleasedStops,
            [
                .. tour.Segments.Select(s => new PickTourSegmentResponse(
                    s.ZoneCode, s.RequestedStrategy, s.AppliedStrategy, s.FellBackBecause, s.Stops))
            ],
            await describe.DescribeAsync(stops, cancellationToken));
    }
}
