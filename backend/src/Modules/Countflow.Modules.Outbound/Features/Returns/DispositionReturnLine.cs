using System.Security.Claims;
using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Features.Returns;

/// <summary>
/// Decides what becomes of a returned quantity, and carries the decision out.
/// <para>
/// The second half of the return, and the reason the whole feature exists. The
/// inspector's findings go to <see cref="IDispositionAdvisor"/>, the rules rank the
/// four options and write their trace, and the winner is posted — unless a person
/// says otherwise, in which case <b>the rules still run and still write their
/// trace</b>, and the disagreement is recorded next to what was disagreed with. An
/// override that skipped the evaluation would destroy the only record of what the
/// system would have done, which is the one thing that makes an override worth
/// reviewing.
/// </para>
/// <para>
/// Each of the four resolves to a movement the ledger already knew how to post
/// before this feature existed. Restock is a condition change and then the ordinary
/// putaway loop; quarantine is a condition change in place, which is what
/// <c>MovementType.StatusChange</c> has meant since Phase 1; repair is a transfer to
/// InTransit; scrap is the write-off Phase 5 built. Nothing here is a new kind of
/// ledger entry, and that is the second half of decision 12's claim — the first
/// half being that the rules engine did not change either.
/// </para>
/// <para>
/// The posting comes before the row, as it does everywhere in this system: a row
/// claiming a disposition that was never posted is a lie, and a movement with no
/// row is a repairable duplicate the next call recognises by key.
/// </para>
/// </summary>
public static class DispositionReturnLine
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/outbound/returns/{id:guid}/lines/{lineId:guid}/disposition", async (
                Guid id,
                Guid lineId,
                DispositionReturnLineRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                OutboundDbContext dbContext,
                ReturnDescriber describe,
                IDispositionAdvisor advisor,
                IStockPosting posting,
                IPutawayAdvisor putaway,
                IWorkQueue work,
                TimeProvider clock,
                ILogger<Return> logger,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(
                        id, lineId, request, user, idempotencyKey, dbContext, describe, advisor,
                        posting, putaway, work, clock, logger, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization()
            .WithTags("Outbound")
            .WithName("DispositionReturnLine")
            .WithSummary("Run the disposition rules over an inspected return line and carry the answer out.")
            .Produces<DispositionReturnLineResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Result<DispositionReturnLineResponse>> HandleAsync(
        Guid id,
        Guid lineId,
        DispositionReturnLineRequest request,
        ClaimsPrincipal user,
        string? idempotencyKey,
        OutboundDbContext dbContext,
        ReturnDescriber describe,
        IDispositionAdvisor advisor,
        IStockPosting posting,
        IPutawayAdvisor putaway,
        IWorkQueue work,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<DispositionReturnLineResponse>(OutboundErrors.IdempotencyKeyRequired);
        }

        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<DispositionReturnLineResponse>(actor.Error);

        var returned = await dbContext.Returns
            .Include(r => r.Lines)
            .ThenInclude(l => l.Dispositions)
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (returned is null) return Result.Failure<DispositionReturnLineResponse>(OutboundErrors.ReturnNotFound);

        var line = returned.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null) return Result.Failure<DispositionReturnLineResponse>(OutboundErrors.ReturnLineNotFound);

        // Checked before anything is posted, and the key is the caller's own. A
        // retried disposition finds its own earlier attempt and replays; a *new*
        // key against a line with nothing left outstanding is a second write-off
        // rather than a duplicate, and is refused below.
        var already = line.Dispositions.SingleOrDefault(d =>
            string.Equals(d.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));

        if (already is not null)
        {
            return await ReplayAsync(already, returned, describe, cancellationToken);
        }

        var quantity = request.Quantity ?? line.OutstandingQuantity;

        if (quantity <= 0m)
        {
            return Result.Failure<DispositionReturnLineResponse>(OutboundErrors.QuantityNotPositive);
        }

        if (quantity > line.OutstandingQuantity)
        {
            return Result.Failure<DispositionReturnLineResponse>(
                OutboundErrors.MoreThanOutstanding(quantity, line.OutstandingQuantity));
        }

        // The rules run whether or not their answer is going to be used. What the
        // system would have done is the only thing that makes an override reviewable.
        var advice = await advisor.AdviseAsync(
            new DispositionQuery
            {
                WarehouseId = returned.WarehouseId,
                ItemId = line.ItemId,
                LotId = line.LotId,
                Quantity = quantity,
                AtLocationId = returned.ReceivedAtLocationId,
                Findings = new InspectionFindings
                {
                    Condition = request.Condition,
                    IsSealed = request.IsSealed,
                    IsRepairable = request.IsRepairable,
                    Notes = request.Notes
                },
                DaysSinceDespatch = await DaysSinceDespatchAsync(
                    dbContext, returned, clock.GetUtcNow(), cancellationToken),
                ExpiryDate = line.ExpiryDate,
                Actor = actor.Value
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<DispositionReturnLineResponse>(advice.Error);

        var applied = request.Disposition ?? advice.Value.Disposition;

        if (applied is not { } disposition)
        {
            // Nothing admissible and nobody overrode it. The trace is the useful
            // half of this answer — it names what ruled each of the four out — so
            // the error carries its id rather than leaving the caller to guess.
            return Result.Failure<DispositionReturnLineResponse>(
                OutboundErrors.NoAdmissibleDisposition(advice.Value.DecisionTraceId));
        }

        // Not a rule, and deliberately not one. A rule set is data a supervisor
        // edits; this is the contract with the ledger — ScrapInstruction.Notes is
        // `required` because a write-off is explained only by the person who
        // ordered it, and an endpoint that let one through empty would be finding a
        // way around that rather than honouring it.
        if (disposition == Disposition.Scrap && string.IsNullOrWhiteSpace(request.Notes))
        {
            return Result.Failure<DispositionReturnLineResponse>(OutboundErrors.ScrapNeedsNotes);
        }

        var wasOverridden = request.Disposition is { } chosen && chosen != advice.Value.Disposition;

        var applying = await ApplyAsync(
            disposition, returned, line, quantity, request, advice.Value.DecisionTraceId,
            idempotencyKey, actor.Value, posting, putaway, work, cancellationToken);

        if (applying.IsFailure) return Result.Failure<DispositionReturnLineResponse>(applying.Error);

        var outcome = applying.Value;

        dbContext.ReturnLineDispositions.Add(new ReturnLineDisposition
        {
            ReturnLineId = line.Id,
            Disposition = disposition,
            Quantity = quantity,
            DecisionTraceId = advice.Value.DecisionTraceId,
            WasOverridden = wasOverridden,
            MovementId = outcome.Movement.MovementId,
            WorkTaskId = outcome.WorkTaskId,
            IdempotencyKey = idempotencyKey,
            DecidedAt = clock.GetUtcNow(),
            DecidedByUserName = actor.Value.UserName,
            Notes = request.Notes
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.Entry(line).Collection(l => l.Dispositions).LoadAsync(cancellationToken);
        await ReturnProjection.RefreshAsync(dbContext, returned, line, clock.GetUtcNow(), cancellationToken);

        logger.LogInformation(
            "Return {Reference} line {Line}: {Quantity} {Disposition}{Override} " +
            "under trace {TraceId} by {User}",
            returned.Reference,
            line.LineNumber,
            quantity,
            disposition,
            wasOverridden ? $" (overriding {advice.Value.Disposition?.ToString() ?? "no advice"})" : string.Empty,
            advice.Value.DecisionTraceId,
            actor.Value.UserName);

        return new DispositionReturnLineResponse(
            await describe.DescribeAsync(returned, cancellationToken),
            disposition,
            advice.Value.Disposition,
            wasOverridden,
            advice.Value.DecisionTraceId,
            advice.Value.Reasons,
            advice.Value.ConsideredCount,
            advice.Value.AdmissibleCount,
            outcome.Movement,
            outcome.WorkTaskId,
            outcome.SuggestedLocationCode);
    }

    /// <summary>What a disposition actually did.</summary>
    private sealed record Applied(PostedMovement Movement, Guid? WorkTaskId, string? SuggestedLocationCode);

    /// <summary>
    /// Turns one of the four verbs into the movement that carries it out.
    /// <para>
    /// Every branch goes through the published posting contract, and not one of them
    /// needed a new kind of ledger entry. That is the point worth noticing: a
    /// disposition is a <i>choice among movements the system already makes</i>, which
    /// is why it is a concept beside <c>StockCondition</c> rather than a set of new
    /// conditions. The condition is what the goods end up in; the disposition is what
    /// was done to them.
    /// </para>
    /// </summary>
    private static async Task<Result<Applied>> ApplyAsync(
        Disposition disposition,
        Return returned,
        ReturnLine line,
        decimal quantity,
        DispositionReturnLineRequest request,
        Guid traceId,
        string idempotencyKey,
        PostingActor actor,
        IStockPosting posting,
        IPutawayAdvisor putaway,
        IWorkQueue work,
        CancellationToken cancellationToken)
    {
        switch (disposition)
        {
            case Disposition.Quarantine:
            {
                // Held where it landed. The returns bay is already a hold-role
                // location, so nothing moves — the goods go from OnHold, which
                // means "nobody has looked at this yet", to Quarantine, which means
                // "somebody has, and it is not going anywhere until a decision is
                // made". Two different claims that happen to sit in the same bay.
                var posted = await posting.ChangeConditionAsync(
                    new ConditionChangeInstruction
                    {
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        LocationId = returned.ReceivedAtLocationId,
                        LicensePlateId = line.LicensePlateId,
                        Quantity = quantity,
                        FromCondition = StockCondition.OnHold,
                        ToCondition = StockCondition.Quarantine,
                        IdempotencyKey = $"{idempotencyKey}:quarantine",
                        Actor = actor,
                        DecisionTraceId = traceId,
                        Notes = request.Notes
                    },
                    cancellationToken);

                return posted.IsFailure
                    ? Result.Failure<Applied>(posted.Error)
                    : new Applied(posted.Value, WorkTaskId: null, SuggestedLocationCode: null);
            }

            case Disposition.Repair:
            {
                var posted = await posting.SendForRepairAsync(
                    new RepairInstruction
                    {
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        FromLocationId = returned.ReceivedAtLocationId,
                        LicensePlateId = line.LicensePlateId,
                        Quantity = quantity,
                        Condition = StockCondition.OnHold,
                        IdempotencyKey = $"{idempotencyKey}:repair",
                        Actor = actor,
                        DecisionTraceId = traceId,
                        Notes = request.Notes ?? $"Returned under {returned.Reference}; sent for repair."
                    },
                    cancellationToken);

                return posted.IsFailure
                    ? Result.Failure<Applied>(posted.Error)
                    : new Applied(posted.Value, WorkTaskId: null, SuggestedLocationCode: null);
            }

            case Disposition.Scrap:
            {
                var posted = await posting.ScrapAsync(
                    new ScrapInstruction
                    {
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        FromLocationId = returned.ReceivedAtLocationId,
                        LicensePlateId = line.LicensePlateId,
                        Condition = StockCondition.OnHold,
                        Quantity = quantity,
                        Cause = request.Condition == StockCondition.Expired
                            ? ScrapCause.Expired
                            : ScrapCause.Damaged,
                        IdempotencyKey = $"{idempotencyKey}:scrap",
                        Actor = actor,

                        // Guaranteed non-empty by the caller. The contract requires
                        // it and so does this endpoint, in two places rather than
                        // one, because the ledger's version of that rule is the one
                        // that has to hold and this one is what turns it into a
                        // coded answer instead of a five-hundred.
                        Notes = request.Notes!
                    },
                    cancellationToken);

                return posted.IsFailure
                    ? Result.Failure<Applied>(posted.Error)
                    : new Applied(posted.Value, WorkTaskId: null, SuggestedLocationCode: null);
            }

            default:
            {
                // Restock, and the only one of the four that is two steps. The
                // condition change is what makes the stock sellable; the putaway is
                // ordinary directed work, chosen and traced by the putaway rules
                // exactly as a dock receipt's is. Two decisions and two traces,
                // which is the honest count — "should this go back on a shelf" and
                // "which shelf" are different questions with different rule sets.
                var posted = await posting.ChangeConditionAsync(
                    new ConditionChangeInstruction
                    {
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        LocationId = returned.ReceivedAtLocationId,
                        LicensePlateId = line.LicensePlateId,
                        Quantity = quantity,
                        FromCondition = StockCondition.OnHold,
                        ToCondition = StockCondition.Available,
                        IdempotencyKey = $"{idempotencyKey}:release",
                        Actor = actor,
                        DecisionTraceId = traceId,
                        Notes = request.Notes
                    },
                    cancellationToken);

                if (posted.IsFailure) return Result.Failure<Applied>(posted.Error);

                var advice = await putaway.SuggestAsync(
                    new PutawayQuery
                    {
                        WarehouseId = returned.WarehouseId,
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        Quantity = quantity,
                        FromLocationId = returned.ReceivedAtLocationId,
                        LicensePlateId = line.LicensePlateId,
                        Condition = StockCondition.Available,
                        Actor = actor
                    },
                    cancellationToken);

                if (advice.IsFailure) return Result.Failure<Applied>(advice.Error);

                var task = await work.EnqueueAsync(
                    new WorkTaskRequest
                    {
                        Kind = WorkTaskKind.Putaway,
                        WarehouseId = returned.WarehouseId,
                        ItemId = line.ItemId,
                        LotId = line.LotId,
                        Quantity = quantity,
                        FromLocationId = returned.ReceivedAtLocationId,
                        SuggestedLocationId = advice.Value.LocationId,
                        LicensePlateId = line.LicensePlateId,
                        DecisionTraceId = advice.Value.DecisionTraceId,
                        Condition = StockCondition.Available,
                        SourceReference = $"RMA {returned.Reference}#{line.LineNumber}",
                        IdempotencyKey = $"{idempotencyKey}:putaway"
                    },
                    cancellationToken);

                if (task.IsFailure) return Result.Failure<Applied>(task.Error);

                // A task raised with no suggestion is the same state a dock
                // receipt reaches when the warehouse is full: the goods are on a
                // shelf either way, the task is visible to a supervisor, and the
                // trace says why nothing was admissible.
                return new Applied(posted.Value, task.Value, advice.Value.LocationCode);
            }
        }
    }

    /// <summary>
    /// How long the customer had the goods, for the freshness rule.
    /// <para>
    /// Read from the order's despatch rather than from its creation, because what
    /// the rule is measuring is time in the customer's hands. Null on a blind return,
    /// and null on an order that never actually went out — both of which the rule
    /// scores at its midpoint rather than inventing a date.
    /// </para>
    /// </summary>
    private static async Task<int?> DaysSinceDespatchAsync(
        OutboundDbContext dbContext,
        Return returned,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (returned.SalesOrderId is not { } orderId)
        {
            return null;
        }

        var despatchedAt = await dbContext.SalesOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => o.DespatchedAt)
            .SingleOrDefaultAsync(cancellationToken);

        return despatchedAt is { } sent
            ? (int)Math.Max(0d, (now - sent).TotalDays)
            : null;
    }

    /// <summary>
    /// The answer to a retry, rebuilt from the row the first attempt wrote.
    /// <para>
    /// The rules are not re-run. Their answer is already on the row's trace, and
    /// running them again against a warehouse that has moved on would produce a
    /// second trace saying something slightly different about a decision that was
    /// made once — which is the shape of thing the journal exists to prevent. The
    /// balances on the echoed movement are zeroed rather than re-read for the same
    /// reason <c>PutawayLicensePlateAsync</c> zeroes its own: they describe the
    /// ledger at the instant of the first posting, and what it holds now is a
    /// different fact that the caller did not ask for.
    /// </para>
    /// </summary>
    private static async Task<Result<DispositionReturnLineResponse>> ReplayAsync(
        ReturnLineDisposition already,
        Return returned,
        ReturnDescriber describe,
        CancellationToken cancellationToken) =>
        new DispositionReturnLineResponse(
            await describe.DescribeAsync(returned, cancellationToken),
            already.Disposition,
            AdvisedDisposition: already.WasOverridden ? null : already.Disposition,
            already.WasOverridden,
            already.DecisionTraceId,
            Reasons: [],
            ConsideredCount: 0,
            AdmissibleCount: 0,
            new PostedMovement(
                already.MovementId,
                Sequence: 0,
                already.DecidedAt,
                already.Quantity,
                LotId: null,
                LicensePlateId: null,
                FromQuantityAfter: 0m,
                ToQuantityAfter: 0m,
                WasAlreadyPosted: true),
            already.WorkTaskId,
            SuggestedLocationCode: null);
}
