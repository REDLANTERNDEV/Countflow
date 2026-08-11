using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <param name="Shortfall">What the rules could not find stock for, across every line.</param>
public sealed record ReleaseOutcome(int Allocations, int Tasks, decimal Shortfall);

/// <summary>
/// Turns an order into committed stock and jobs in somebody's queue.
/// <para>
/// The outbound counterpart of the dock receipt, and the same discipline: every
/// step goes through a published contract. <see cref="IAllocationAdvisor"/>
/// chooses the stock and writes the trace, <see cref="IStockReservations"/> holds
/// it, <see cref="IWorkQueue"/> raises the picks. This module decides <i>when</i>
/// those happen and remembers what it asked for; it moves no stock and evaluates
/// no rules.
/// </para>
/// <para>
/// <b>Why this is a service and not an endpoint.</b> Releasing a wave is releasing
/// its orders, and a wave that released them through a second, slightly different
/// path would be a second definition of what "released" means.
/// </para>
/// <para>
/// <b>The order of the three writes, and what a crash between them leaves.</b>
/// Reserve, then record, then raise. A crash after reserving leaves stock held
/// that this module has no record of — which the next release repairs, because it
/// asks the reservation ledger what it is still holding for each line and lets go
/// of anything its own books do not claim. A crash after recording leaves a
/// commitment with no job, which the next release also repairs, because raising
/// the picks is driven off the allocation rows rather than off what this run just
/// created. Both are convergent: running release again is always safe and always
/// moves the order closer to fully released.
/// </para>
/// </summary>
public sealed class OrderRelease(
    OutboundDbContext dbContext,
    IAllocationAdvisor advisor,
    IStockReservations reservations,
    IWorkQueue work,
    TimeProvider clock,
    ILogger<OrderRelease> logger)
{
    /// <summary>
    /// Errors that mean somebody else got there first, rather than that something
    /// is wrong.
    /// <para>
    /// The rules chose a holding and, in the moment between choosing it and
    /// holding it, another order took the last of it. That is an ordinary race in
    /// a busy warehouse and not a reason to fail a release: the line goes short,
    /// which is exactly what happened, and releasing again picks the shortfall up
    /// against whatever is there now. Every other failure is a defect and is
    /// allowed to surface as one.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> LostTheRace = new(StringComparer.Ordinal)
    {
        "inventory.insufficient_available_stock",
        "inventory.holding_not_found"
    };

    public async Task<Result<ReleaseOutcome>> ReleaseAsync(
        SalesOrder order,
        string idempotencyKey,
        PostingActor actor,
        CancellationToken cancellationToken)
    {
        if (order.Status is not (SalesOrderStatus.New or SalesOrderStatus.Released))
        {
            return Result.Failure<ReleaseOutcome>(OutboundErrors.OrderNotReleasable(order.Status.ToString()));
        }

        if (order.StagingLocationId is null)
        {
            return Result.Failure<ReleaseOutcome>(OutboundErrors.StagingLocationRequired);
        }

        var allocationsRaised = 0;
        var tasksRaised = 0;
        var shortfall = 0m;

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            var released = await ReleaseLineAsync(order, line, idempotencyKey, actor, cancellationToken);
            if (released.IsFailure)
            {
                return Result.Failure<ReleaseOutcome>(released.Error);
            }

            allocationsRaised += released.Value.Allocations;
            tasksRaised += released.Value.Tasks;
            shortfall += released.Value.Shortfall;
        }

        order.Status = SalesOrderStatus.Released;
        order.ReleasedAt ??= clock.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Released {Reference}: {Allocations} allocations, {Tasks} picks, {Shortfall} short",
            order.Reference,
            allocationsRaised,
            tasksRaised,
            shortfall);

        return new ReleaseOutcome(allocationsRaised, tasksRaised, shortfall);
    }

    private async Task<Result<ReleaseOutcome>> ReleaseLineAsync(
        SalesOrder order,
        SalesOrderLine line,
        string idempotencyKey,
        PostingActor actor,
        CancellationToken cancellationToken)
    {
        var reference = $"{order.Reference}#{line.LineNumber}";

        await LetGoOfUnclaimedHoldsAsync(line, reference, cancellationToken);

        var allocations = 0;
        var shortfall = 0m;

        // Nothing to allocate if a previous run of *this* release already did the
        // line. The tasks are still raised below, from the rows rather than from
        // what this pass created, so an interrupted run finishes rather than
        // stopping short.
        var alreadyThisRelease = line.Allocations
            .Any(a => string.Equals(a.ReleaseIdempotencyKey, idempotencyKey, StringComparison.Ordinal));

        var outstanding = line.OrderedQuantity - line.Allocations.Sum(a => a.Quantity);

        if (!alreadyThisRelease && outstanding > 0m)
        {
            var plan = await advisor.AllocateAsync(
                new AllocationQuery
                {
                    WarehouseId = order.WarehouseId,
                    ItemId = line.ItemId,
                    RequiredLotId = line.RequiredLotId,
                    Quantity = outstanding,
                    ShipBy = order.ShipBy,
                    Actor = actor
                },
                cancellationToken);

            if (plan.IsFailure)
            {
                return Result.Failure<ReleaseOutcome>(plan.Error);
            }

            shortfall = plan.Value.ShortfallQuantity;

            foreach (var chosen in plan.Value.Lines)
            {
                var reserved = await reservations.ReserveAsync(
                    new ReservationInstruction
                    {
                        BalanceId = chosen.BalanceId,
                        Quantity = chosen.Quantity,
                        OwnerId = line.Id,
                        OwnerReference = reference,
                        DecisionTraceId = plan.Value.DecisionTraceId,

                        // Keyed on the holding rather than on the position in the
                        // plan. A retry re-runs the rules against a warehouse that
                        // has moved on, so plan line 2 may be a different holding
                        // than it was — and a positional key would then hand back
                        // the first attempt's reservation for the second attempt's
                        // stock.
                        IdempotencyKey = $"{idempotencyKey}:{line.Id}:{chosen.BalanceId}",
                        Actor = actor
                    },
                    cancellationToken);

                if (reserved.IsFailure)
                {
                    if (!LostTheRace.Contains(reserved.Error.Code))
                    {
                        return Result.Failure<ReleaseOutcome>(reserved.Error);
                    }

                    logger.LogWarning(
                        "Lost {Quantity} of {Reference} at {Location} between advice and reservation: {Error}",
                        chosen.Quantity,
                        reference,
                        chosen.LocationCode,
                        reserved.Error.Code);

                    shortfall += chosen.Quantity;
                    continue;
                }

                // Added through the *set*, not through `line.Allocations`. A new
                // entity discovered through a navigation on an already-tracked
                // parent is inferred to be Modified rather than Added when its key
                // is already populated — and every key here is a client-generated
                // UUIDv7 — so EF issues an UPDATE against a row that does not
                // exist and the save fails with a concurrency error. Adding to the
                // set states the intent; the collection is fixed up from the
                // foreign key either way, so the rows are still visible to the
                // pick-raising below.
                var allocation = new OrderLineAllocation
                {
                    SalesOrderLineId = line.Id,
                    ReleaseIdempotencyKey = idempotencyKey,
                    Quantity = chosen.Quantity,
                    StockReservationId = reserved.Value.Id,
                    DecisionTraceId = plan.Value.DecisionTraceId,
                    LocationId = chosen.LocationId,
                    LocationCode = chosen.LocationCode,
                    LotId = chosen.LotId,
                    LotNumber = chosen.LotNumber,
                    ExpiryDate = chosen.ExpiryDate,
                    AllocatedAt = clock.GetUtcNow()
                };

                dbContext.OrderLineAllocations.Add(allocation);
                allocations++;
            }

            // Saved before any task is raised, so a job never points at a
            // commitment this module has not written down.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var tasks = await RaisePicksAsync(order, line, reference, idempotencyKey, cancellationToken);
        if (tasks.IsFailure)
        {
            return Result.Failure<ReleaseOutcome>(tasks.Error);
        }

        // Re-derived from the rows rather than incremented. See
        // SalesOrderLine.AllocatedQuantity — a running total cannot be made
        // idempotent, and this is the third table in the system to make the point.
        line.AllocatedQuantity = line.Allocations.Sum(a => a.Quantity);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ReleaseOutcome(allocations, tasks.Value, shortfall);
    }

    /// <summary>
    /// Releases commitments this module's own books do not claim.
    /// <para>
    /// The repair for a release interrupted between reserving stock and recording
    /// that it had. Those holds are invisible to everything here — no allocation
    /// row points at them — so they would sit against the stock forever,
    /// quietly making a warehouse look emptier than it is. Running before the
    /// rules are asked means the freed stock is available to the allocation that
    /// is about to happen.
    /// </para>
    /// </summary>
    private async Task LetGoOfUnclaimedHoldsAsync(
        SalesOrderLine line,
        string reference,
        CancellationToken cancellationToken)
    {
        var open = await reservations.FindOpenByOwnerAsync(line.Id, cancellationToken);
        if (open.Count == 0)
        {
            return;
        }

        var claimed = line.Allocations.Select(a => a.StockReservationId).ToHashSet();
        var orphans = open.Where(r => !claimed.Contains(r.Id)).Select(r => r.Id).ToList();

        if (orphans.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Releasing {Count} holds on {Reference} that no allocation claims — a previous release was interrupted",
            orphans.Count,
            reference);

        await reservations.ReleaseAsync(orphans, "outbound.interrupted_release", cancellationToken);
    }

    /// <summary>
    /// Raises a pick for every commitment that has not got one yet.
    /// <para>
    /// Driven off the allocation rows rather than off what was just created, which
    /// is what makes an interrupted release finish rather than skip. One job per
    /// commitment: a pick's unit of work is the order line's claim on one holding,
    /// so there is nothing here to merge and nothing for the handheld to learn.
    /// </para>
    /// </summary>
    private async Task<Result<int>> RaisePicksAsync(
        SalesOrder order,
        SalesOrderLine line,
        string reference,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var raised = 0;

        foreach (var allocation in line.Allocations.Where(a => a.WorkTaskId is null).OrderBy(a => a.AllocatedAt))
        {
            var task = await work.EnqueueAsync(
                new WorkTaskRequest
                {
                    Kind = WorkTaskKind.Pick,
                    WarehouseId = order.WarehouseId,
                    ItemId = line.ItemId,
                    LotId = allocation.LotId,
                    Quantity = allocation.Quantity,
                    FromLocationId = allocation.LocationId,

                    // Where to drop it, not where the rules chose — the choosing
                    // happened when the stock was picked. The field means "where
                    // this job ends", and an operator dropping somewhere else is
                    // recorded as an override exactly as it is for a putaway.
                    SuggestedLocationId = order.StagingLocationId,
                    DecisionTraceId = allocation.DecisionTraceId,
                    StockReservationId = allocation.StockReservationId,
                    Priority = order.Priority,
                    SourceReference = reference,
                    IdempotencyKey = $"{idempotencyKey}:{allocation.Id}:pick"
                },
                cancellationToken);

            if (task.IsFailure)
            {
                return Result.Failure<int>(task.Error);
            }

            allocation.WorkTaskId = task.Value;
            raised++;
        }

        return raised;
    }
}
