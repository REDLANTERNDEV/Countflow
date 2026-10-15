using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Countflow.Modules.Inventory.Counting;

/// <summary>
/// The only way a count is recorded.
/// <para>
/// The ledger's sibling in the sense <c>ReservationLedger</c> is, and its opposite
/// in the sense that matters: a reservation is a promise about stock that has not
/// moved, and a count is a statement that stock has not moved. Neither is a ledger
/// entry, both live in this schema, and both are here because they have to settle
/// in the same transaction as a movement they may produce.
/// </para>
/// <para>
/// <b>The variance is computed here and never supplied.</b> The caller says what
/// was found; this reads what was believed and subtracts. That is the whole reason
/// <c>IStockPosting</c> exposes <c>CountAsync</c> rather than letting the work
/// queue call the adjustment endpoint with a number it worked out itself — an
/// adjustment a caller can name the size of is an adjustment that can be any size,
/// and the count would stop being evidence for the correction it caused.
/// </para>
/// <para>
/// <b>The book is read before the lock, deliberately.</b> A count is an observation
/// of a shelf at a moment, and a movement between the observation and the posting
/// is a real movement rather than a race to be excluded: if a picker takes ten
/// cases out of a bay the operator has just counted, the correction is still
/// <c>counted − expected</c>, because the pick is separately and correctly in the
/// ledger. Applying the difference as a delta is what makes that true. What is not
/// robust is the direction that would drive a shelf negative — a count that finds
/// less than the book, where the book has since dropped further — and that fails
/// loudly in the ledger's own sufficiency check, which is the right answer: the
/// observation is stale and the bay wants recounting.
/// </para>
/// </summary>
public sealed class StockCounting(
    InventoryDbContext dbContext,
    StockLedger ledger,
    StockReferenceResolver resolve,
    IItemDirectory items,
    ILocationDirectory locations,
    TimeProvider clock,
    ILogger<StockCounting> logger)
{
    public async Task<Result<CountOutcome>> CountAsync(
        CountInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instruction.IdempotencyKey))
        {
            return Result.Failure<CountOutcome>(InventoryErrors.IdempotencyKeyRequired);
        }

        if (instruction.CountedQuantity < 0m)
        {
            return Result.Failure<CountOutcome>(InventoryErrors.CountedQuantityNegative);
        }

        // Checked first, and before anything is read. A handheld retries whatever it
        // did not hear the answer to, and a count is no different from a movement in
        // that respect — except that a replayed count has to hand back the same
        // variance it found the first time rather than recomputing one against a
        // book that has moved on since.
        var already = await FindByKeyAsync(instruction.IdempotencyKey, cancellationToken);
        if (already is not null)
        {
            return await ReplayAsync(instruction, already, cancellationToken);
        }

        var validated = await ValidateAsync(instruction, cancellationToken);
        if (validated.IsFailure) return Result.Failure<CountOutcome>(validated.Error);

        var location = validated.Value;
        var status = ToStatus(instruction.Condition);
        var holding = new Holding(instruction.LocationId, instruction.LicensePlateId, status);

        var book = await ReadBookAsync(instruction.ItemId, instruction.LotId, holding, cancellationToken);

        var variance = instruction.CountedQuantity - book.Quantity;

        var count = new StockCount
        {
            WarehouseId = location.WarehouseId,
            ItemId = instruction.ItemId,
            LotId = instruction.LotId,
            LocationId = instruction.LocationId,
            LicensePlateId = instruction.LicensePlateId,
            Status = status,
            ExpectedQuantity = book.Quantity,
            CountedQuantity = instruction.CountedQuantity,
            Variance = variance,
            AtMovementSequence = book.LastMovementSequence,
            WorkTaskId = instruction.TaskId,
            DecisionTraceId = instruction.DecisionTraceId,
            IdempotencyKey = instruction.IdempotencyKey,
            CountedByUserId = instruction.Actor.UserId,
            CountedByUserName = instruction.Actor.UserName,
            CountedAt = clock.GetUtcNow(),
            Notes = instruction.Notes
        };

        return variance == 0m
            ? await RecordAgreementAsync(count, cancellationToken)
            : await RecordVarianceAsync(count, location, holding, cancellationToken);
    }

    /// <summary>
    /// The shelf agreed with the book, so there is nothing to post and only the
    /// observation to write.
    /// <para>
    /// This is the common case and the valuable one. It writes no ledger entry, and
    /// that is exactly why the count needed a table of its own: accuracy is
    /// verified over counted, and a system that recorded only the counts that went
    /// wrong would know its numerator and have to guess at its denominator.
    /// </para>
    /// </summary>
    private async Task<Result<CountOutcome>> RecordAgreementAsync(
        StockCount count,
        CancellationToken cancellationToken)
    {
        dbContext.StockCounts.Add(count);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Two copies of one retried confirmation arrived close enough together
            // that both got past the check above. The unique index settles it and
            // the loser reports the winner's finding.
            dbContext.ChangeTracker.Clear();

            var winner = await FindByKeyAsync(count.IdempotencyKey, cancellationToken);

            return winner is null
                ? Result.Failure<CountOutcome>(InventoryErrors.IdempotencyKeyReused)
                : Describe(winner, adjustment: null, wasAlready: true);
        }

        logger.LogInformation(
            "Counted {Counted} of item {ItemId} at location {LocationId}: agrees with the book (count {CountId})",
            count.CountedQuantity,
            count.ItemId,
            count.LocationId,
            count.Id);

        return Describe(count, adjustment: null, wasAlready: false);
    }

    /// <summary>
    /// The shelf and the book disagree, so the difference is posted against the
    /// Adjustment counterparty and the count row travels with it.
    /// <para>
    /// Which side the adjustment account sits on is the sign of the variance, and
    /// nothing else. Found more than expected is stock arriving from the adjustment
    /// account — the common case, and how a warehouse discovers a mis-pick from
    /// three weeks ago rather than a data-entry error to be refused. Found less is
    /// stock leaving to it.
    /// </para>
    /// </summary>
    private async Task<Result<CountOutcome>> RecordVarianceAsync(
        StockCount count,
        LocationSnapshot location,
        Holding holding,
        CancellationToken cancellationToken)
    {
        var adjustment = await resolve.CounterpartyAsync(
            location.WarehouseId, CounterpartyKind.Adjustment, cancellationToken);

        if (adjustment.IsFailure) return Result.Failure<CountOutcome>(adjustment.Error);

        var found = count.Variance > 0m;

        // The adjustment account holds stock in whatever status the shelf does, so
        // the contra entry mirrors the holding rather than normalising to Available:
        // a quarantined pallet that turns out to be short is a quarantine loss, and
        // an adjustment account that flattened it would report the shrinkage against
        // sellable stock.
        var counterparty = new Holding(adjustment.Value.Id, null, holding.Status);

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = count.ItemId,
                LotId = count.LotId,
                Quantity = Math.Abs(count.Variance),
                From = found ? counterparty : holding,
                To = found ? holding : counterparty,
                Type = MovementType.CountAdjustment,
                Reason = MovementReason.CycleCountVariance,
                IdempotencyKey = count.IdempotencyKey,
                Actor = new LedgerActor(count.CountedByUserId, count.CountedByUserName),
                TaskId = count.WorkTaskId,
                DecisionTraceId = count.DecisionTraceId,
                RecordsCount = count,
                Notes = count.Notes
            },
            cancellationToken);

        if (posted.IsFailure) return Result.Failure<CountOutcome>(posted.Error);

        logger.LogWarning(
            "Count variance at location {LocationId} for item {ItemId}: book said {Expected}, " +
            "operator {User} found {Counted} ({Variance:+#;-#;0}), posted as #{Sequence}",
            count.LocationId,
            count.ItemId,
            count.ExpectedQuantity,
            count.CountedByUserName,
            count.CountedQuantity,
            count.Variance,
            posted.Value.Sequence);

        return Describe(count, posted.Value, posted.Value.WasAlreadyPosted);
    }

    /// <summary>
    /// What the book says about a holding, and where the ledger stood when it said
    /// it.
    /// <para>
    /// A holding with no balance row reads as zero at sequence zero, which is not a
    /// missing value: it is the system stating that the bay is empty, and a count
    /// that finds twelve cases there is the most valuable count in the building.
    /// </para>
    /// </summary>
    private async Task<BookQuantity> ReadBookAsync(
        Guid itemId,
        Guid? lotId,
        Holding holding,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b =>
                b.ItemId == itemId
                && ((b.LotId == null && lotId == null) || b.LotId == lotId)
                && b.LocationId == holding.LocationId
                && ((b.LicensePlateId == null && holding.LicensePlateId == null)
                    || b.LicensePlateId == holding.LicensePlateId)
                && b.Status == holding.Status)
            .Select(b => new BookQuantity(b.Quantity, b.LastMovementSequence))
            .SingleOrDefaultAsync(cancellationToken);

        return row == default ? new BookQuantity(0m, 0L) : row;
    }

    /// <summary>
    /// Everything that can be checked before anything is written, and the same
    /// checks the ledger would make — run here as well because a count that agrees
    /// with the book never reaches the ledger, and a bay recorded as verified for
    /// an item that does not exist is worse than a rejected scan.
    /// </summary>
    private Task<Result<LocationSnapshot>> ValidateAsync(CountInstruction instruction, CancellationToken cancellationToken) =>
        ValidateHoldingAsync(instruction.ItemId, instruction.LotId, instruction.LocationId, cancellationToken);

    internal async Task<Result<LocationSnapshot>> ValidateHoldingAsync(
        Guid itemId, Guid? lotId, Guid locationId,
        CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(itemId, cancellationToken);
        if (item is null) return Result.Failure<LocationSnapshot>(InventoryErrors.ItemNotFound);

        if (item.IsLotTracked && lotId is null)
        {
            return Result.Failure<LocationSnapshot>(InventoryErrors.LotRequired);
        }

        if (!item.IsLotTracked && lotId is not null)
        {
            return Result.Failure<LocationSnapshot>(InventoryErrors.LotNotPermitted);
        }

        if (lotId is { } knownLotId
            && !await dbContext.Lots.AnyAsync(
                l => l.Id == knownLotId && l.ItemId == itemId, cancellationToken))
        {
            return Result.Failure<LocationSnapshot>(InventoryErrors.LotNotFound);
        }

        var location = await locations.FindAsync(locationId, cancellationToken);
        if (location is null) return Result.Failure<LocationSnapshot>(InventoryErrors.LocationNotFound);

        // A counterparty is an account, not a shelf. Nobody can stand in front of
        // the supplier balance and report what is on it, and a "variance" against
        // one would be an adjustment posted against an adjustment.
        return location.IsVirtual
            ? Result.Failure<LocationSnapshot>(InventoryErrors.CannotCountCounterparty(location.Code))
            : Result.Success(location);
    }

    private async Task<StockCount?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.StockCounts
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.IdempotencyKey == key, cancellationToken);

    /// <summary>
    /// Hands back the finding the first attempt recorded, with the movement it
    /// posted if there was one.
    /// <para>
    /// Refused rather than replayed when the key was used for a different holding:
    /// reporting the old count would tell the operator that the bay they are
    /// standing in front of has been verified, when a different one was.
    /// </para>
    /// </summary>
    private async Task<Result<CountOutcome>> ReplayAsync(
        CountInstruction instruction,
        StockCount existing,
        CancellationToken cancellationToken)
    {
        var matches = existing.ItemId == instruction.ItemId
                      && existing.LotId == instruction.LotId
                      && existing.LocationId == instruction.LocationId
                      && existing.LicensePlateId == instruction.LicensePlateId
                      && existing.Status == ToStatus(instruction.Condition)
                      && existing.CountedQuantity == instruction.CountedQuantity;

        if (!matches)
        {
            logger.LogWarning(
                "Idempotency key {Key} reused for a different count than {CountId}",
                instruction.IdempotencyKey,
                existing.Id);

            return Result.Failure<CountOutcome>(InventoryErrors.IdempotencyKeyReused);
        }

        if (existing.AdjustmentMovementId is not { } movementId)
        {
            return Describe(existing, adjustment: null, wasAlready: true);
        }

        var movement = await dbContext.StockMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == movementId, cancellationToken);

        return movement is null
            ? Result.Failure<CountOutcome>(InventoryErrors.MovementNotFound)
            : Describe(
                existing,
                new PostedMovement(
                    movement.Id,
                    movement.Sequence,
                    movement.OccurredAt,
                    movement.Quantity,
                    movement.LotId,
                    movement.ToLicensePlateId,
                    FromQuantityAfter: 0m,
                    ToQuantityAfter: 0m,
                    WasAlreadyPosted: true),
                wasAlready: true);
    }

    private static CountOutcome Describe(StockCount count, PostedMovement? adjustment, bool wasAlready) =>
        new(count.Id, count.ExpectedQuantity, count.CountedQuantity, count.Variance, adjustment, wasAlready);

    private static PostedMovement Describe(MovementReceipt receipt, StockCount count) =>
        new(
            receipt.MovementId,
            receipt.Sequence,
            receipt.OccurredAt,
            receipt.Quantity,
            count.LotId,
            count.LicensePlateId,
            receipt.FromQuantityAfter,
            receipt.ToQuantityAfter,
            receipt.WasAlreadyPosted);

    private static CountOutcome Describe(StockCount count, MovementReceipt receipt, bool wasAlready) =>
        new(
            count.Id,
            count.ExpectedQuantity,
            count.CountedQuantity,
            count.Variance,
            Describe(receipt, count),
            wasAlready);

    private static InventoryStatus ToStatus(StockCondition condition) => condition switch
    {
        StockCondition.Quarantine => InventoryStatus.Quarantine,
        StockCondition.Damaged => InventoryStatus.Damaged,
        StockCondition.OnHold => InventoryStatus.OnHold,
        StockCondition.Expired => InventoryStatus.Expired,
        _ => InventoryStatus.Available
    };

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private readonly record struct BookQuantity(decimal Quantity, long LastMovementSequence);
}
