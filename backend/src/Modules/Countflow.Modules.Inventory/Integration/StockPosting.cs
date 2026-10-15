using Countflow.Modules.Inventory.Counting;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published write surface (ADR 0002) — how the inbound loop and the
/// work queue move stock without being able to reach the ledger.
/// <para>
/// An adapter, and nothing more. Every method here translates an operation into
/// a <see cref="PostingCommand"/> and hands it to <see cref="StockLedger"/>. It
/// holds no invariant of its own, because a second place that enforced them is a
/// second place that can stop enforcing them.
/// </para>
/// <para>
/// What it does own is the ledger vocabulary: the caller says "put this away" and
/// this decides that means a <see cref="MovementType.Putaway"/> for reason
/// <see cref="MovementReason.PutawayComplete"/>. Keeping that mapping inside the
/// module is why the contract exposes operations rather than movement types.
/// </para>
/// </summary>
public sealed class StockPosting(
    InventoryDbContext dbContext,
    StockLedger ledger,
    ReservationLedger reservations,
    StockCounting counting,
    StockReferenceResolver resolve,
    IItemDirectory items,
    ILocationDirectory locations) : IStockPosting
{
    public async Task<Result<PostedMovement>> ReceiveAsync(
        ReceiptInstruction instruction,
        CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(instruction.ItemId, cancellationToken);
        if (item is null) return Result.Failure<PostedMovement>(InventoryErrors.ItemNotFound);

        var destination = await locations.FindAsync(instruction.ToLocationId, cancellationToken);
        if (destination is null) return Result.Failure<PostedMovement>(InventoryErrors.LocationNotFound);

        // The counterparty account belongs to the warehouse the goods are landing
        // in, so it is derived from the destination rather than asked for — and
        // *which* account it is follows from where the goods came from. A return
        // credits Customer, which is the account the despatch debited; anything
        // else credits Supplier. Booking a return against Supplier is what this
        // system did until Phase 7, and it left the customer account growing
        // forever with nothing that could ever bring it back down.
        var counterparty = await resolve.CounterpartyAsync(
            destination.WarehouseId,
            instruction.IsReturn ? CounterpartyKind.Customer : CounterpartyKind.Supplier,
            cancellationToken);
        if (counterparty.IsFailure) return Result.Failure<PostedMovement>(counterparty.Error);

        var lot = await resolve.LotAsync(
            item,
            instruction.LotNumber,
            instruction.ExpiryDate,
            instruction.ProductionDate,
            createIfMissing: true,
            cancellationToken);
        if (lot.IsFailure) return Result.Failure<PostedMovement>(lot.Error);

        var plate = await resolve.LicensePlateAsync(instruction.Sscc, createIfMissing: true, cancellationToken);
        if (plate.IsFailure) return Result.Failure<PostedMovement>(plate.Error);

        var status = ToStatus(instruction.Condition);

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = item.Id,
                LotId = lot.Value,
                Quantity = instruction.Quantity,
                // Stock always leaves the counterparty account as Available: from
                // the other side it was good stock, and booking it into quarantine
                // is a decision this warehouse made. That holds for a return too —
                // the customer sent back what they believed they had bought, and
                // what it actually turns out to be is what the inspection is for.
                From = new Holding(counterparty.Value.Id, null, InventoryStatus.Available),
                To = new Holding(destination.Id, plate.Value, status),
                Type = MovementType.Receipt,
                Reason = instruction.IsReturn
                    ? MovementReason.CustomerReturn
                    : MovementReason.PurchaseOrderReceipt,
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                TaskId = instruction.TaskId,
                Notes = instruction.Notes
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, lot.Value, plate.Value);
    }

    public async Task<Result<PostedMovement>> PutawayAsync(
        PutawayInstruction instruction,
        CancellationToken cancellationToken)
    {
        var status = ToStatus(instruction.Condition);

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = instruction.ItemId,
                LotId = instruction.LotId,
                Quantity = instruction.Quantity,
                // The licence plate travels with the goods: a pallet put into a
                // rack is the same pallet it was on the dock. A putaway that
                // dropped the plate would strip the pallet of its identity halfway
                // through the one move that identity exists to track.
                From = new Holding(instruction.FromLocationId, instruction.LicensePlateId, status),
                To = new Holding(instruction.ToLocationId, instruction.LicensePlateId, status),
                Type = MovementType.Putaway,
                Reason = MovementReason.PutawayComplete,
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                TaskId = instruction.TaskId,
                DecisionTraceId = instruction.DecisionTraceId,
                Notes = instruction.Notes
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, instruction.LotId, instruction.LicensePlateId);
    }

    public async Task<Result<IReadOnlyList<PostedMovement>>> PutawayLicensePlateAsync(
        PlatePutawayInstruction instruction,
        CancellationToken cancellationToken)
    {
        var prefix = instruction.IdempotencyKey + ":";

        // Checked before the contents are read, and this ordering is the whole
        // trick. A retry arrives *after* the first attempt moved the pallet, so
        // reading the source location would find it empty and report an error for
        // work that in fact succeeded. Recognising the key family first turns that
        // into the replay it actually is.
        var already = await dbContext.StockMovements
            .AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix))
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        if (already.Count > 0)
        {
            return Result.Success<IReadOnlyList<PostedMovement>>(
            [
                .. already.Select(m => new PostedMovement(
                    m.Id, m.Sequence, m.OccurredAt, m.Quantity, m.LotId, m.ToLicensePlateId,
                    FromQuantityAfter: 0m, ToQuantityAfter: 0m, WasAlreadyPosted: true))
            ]);
        }

        var lines = await dbContext.StockBalances
            .AsNoTracking()
            .Where(b => b.LicensePlateId == instruction.LicensePlateId
                        && b.LocationId == instruction.FromLocationId
                        && b.Quantity > 0)
            // Deterministic, because the per-line idempotency keys are derived from
            // this order. Two attempts that ordered the contents differently would
            // hand the same movement two different keys and post it twice.
            .OrderBy(b => b.ItemId)
            .ThenBy(b => b.LotId)
            .ThenBy(b => b.Status)
            .Select(b => new { b.ItemId, b.LotId, b.Status, b.Quantity })
            .ToListAsync(cancellationToken);

        if (lines.Count == 0)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>(InventoryErrors.LicensePlateEmpty);
        }

        var commands = lines.Select((line, index) => new PostingCommand
        {
            ItemId = line.ItemId,
            LotId = line.LotId,
            Quantity = line.Quantity,
            From = new Holding(instruction.FromLocationId, instruction.LicensePlateId, line.Status),
            To = new Holding(instruction.ToLocationId, instruction.LicensePlateId, line.Status),
            Type = MovementType.Putaway,
            Reason = MovementReason.PutawayComplete,
            IdempotencyKey = $"{prefix}{index:D3}",
            Actor = ToActor(instruction.Actor),
            TaskId = instruction.TaskId,
            DecisionTraceId = instruction.DecisionTraceId,
            Notes = instruction.Notes
        }).ToList();

        var posted = await ledger.PostBatchAsync(commands, cancellationToken);
        if (posted.IsFailure)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>(posted.Error);
        }

        return Result.Success<IReadOnlyList<PostedMovement>>(
        [
            .. posted.Value.Select((receipt, index) =>
                Describe(receipt, commands[index].LotId, instruction.LicensePlateId))
        ]);
    }

    /// <summary>
    /// Takes the reserved stock to the bench, and closes the claim that was
    /// holding it.
    /// <para>
    /// Everything about what is being moved comes from the reservation: the
    /// caller supplies only where the operator dropped it. That is what makes
    /// "you cannot pick stock nobody promised" a property of the type rather than
    /// a check somebody remembered to write.
    /// </para>
    /// <para>
    /// The goods come off their licence plate. A picker takes cases from a pallet
    /// into a tote, and the pallet they came from is not the thing that arrives at
    /// the customer — so the plate travels on a putaway, which moves the pallet,
    /// and is dropped by a pick, which breaks into it. A whole-pallet pick empties
    /// the plate for the same reason, which is the honest description of what
    /// happened to it.
    /// </para>
    /// </summary>
    public Task<Result<PostedMovement>> PickAsync(PickInstruction instruction, CancellationToken cancellationToken) =>
        ReservedMoveAsync(instruction, MovementType.Pick, MovementReason.OrderPick, cancellationToken);

    private async Task<Result<PostedMovement>> ReservedMoveAsync(PickInstruction instruction,
        MovementType type, MovementReason reason, CancellationToken cancellationToken)
    {
        var reservation = await reservations.FindAsync(instruction.ReservationId, cancellationToken);
        if (reservation is null)
        {
            return Result.Failure<PostedMovement>(InventoryErrors.ReservationNotFound);
        }

        // The ledger checks consumption inside its posting transaction. A claim
        // already consumed under this same command key must still replay after
        // a lost acknowledgement; an early state refusal would prevent that.
        var holding = reservation.Balance!;

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = holding.ItemId,
                LotId = holding.LotId,
                Quantity = reservation.Quantity,
                From = new Holding(holding.LocationId, holding.LicensePlateId, holding.Status),
                To = new Holding(instruction.ToLocationId, null, holding.Status),
                Type = type,
                Reason = reason,
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                TaskId = instruction.TaskId,
                DecisionTraceId = instruction.DecisionTraceId,
                ConsumesReservationId = reservation.Id,
                Notes = instruction.Notes ?? reservation.OwnerReference
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, holding.LotId, licensePlateId: null);
    }

    public async Task<Result<PostedMovement>> ReplenishAsync(PickInstruction instruction, CancellationToken cancellationToken)
    {
        var reservation = await reservations.FindAsync(instruction.ReservationId, cancellationToken);
        if (reservation?.Balance is not { } balance)
            return Result.Failure<PostedMovement>(InventoryErrors.ReservationNotFound);
        if (reservation.State != ReservationState.Open)
            return await ReservedMoveAsync(instruction, MovementType.Replenishment, MovementReason.ReplenishmentPull, cancellationToken);
        var storage = await locations.FindStorageLocationsAsync([balance.LocationId, instruction.ToLocationId], cancellationToken);
        var source = storage.GetValueOrDefault(balance.LocationId);
        var target = storage.GetValueOrDefault(instruction.ToLocationId);
        if (source is not { Role: StorageRole.Reserve, IsActive: true }
            || target is not { Role: StorageRole.Forward, IsActive: true, IsReplenishable: true }
            || source.WarehouseId != target.WarehouseId)
            return Result.Failure<PostedMovement>(InventoryErrors.ReplenishmentLocationsInvalid);
        return await ReservedMoveAsync(instruction, MovementType.Replenishment, MovementReason.ReplenishmentPull, cancellationToken);
    }

    /// <summary>
    /// Sends the packed order out of the building, every line together.
    /// <para>
    /// The customer account belongs to the warehouse the goods are leaving, so it
    /// is derived from the origin rather than asked for — the mirror of
    /// <see cref="ReceiveAsync"/>, and what makes the round trip from Supplier to
    /// Customer sum to zero.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<PostedMovement>>> DespatchAsync(
        DespatchInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (instruction.Lines.Count == 0)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>(InventoryErrors.NothingToDespatch);
        }

        var origin = await locations.FindAsync(instruction.FromLocationId, cancellationToken);
        if (origin is null) return Result.Failure<IReadOnlyList<PostedMovement>>(InventoryErrors.LocationNotFound);

        var customer = await resolve.CounterpartyAsync(
            origin.WarehouseId, CounterpartyKind.Customer, cancellationToken);
        if (customer.IsFailure) return Result.Failure<IReadOnlyList<PostedMovement>>(customer.Error);

        var commands = instruction.Lines.Select((line, index) =>
        {
            var status = ToStatus(line.Condition);

            return new PostingCommand
            {
                ItemId = line.ItemId,
                LotId = line.LotId,
                Quantity = line.Quantity,
                From = new Holding(instruction.FromLocationId, null, status),
                To = new Holding(customer.Value.Id, null, status),
                Type = MovementType.Despatch,
                Reason = MovementReason.CustomerDespatch,

                // Positional, and safe to be: the lines are the caller's own list
                // in its own order, and a retry of the same despatch hands over
                // the same list. A batch that derived keys from the ledger's state
                // instead would renumber itself the moment anything else posted.
                IdempotencyKey = $"{instruction.IdempotencyKey}:{index:D3}",
                Actor = ToActor(instruction.Actor),
                Notes = instruction.Notes ?? instruction.Reference
            };
        }).ToList();

        var posted = await ledger.PostBatchAsync(commands, cancellationToken);

        return posted.IsFailure
            ? Result.Failure<IReadOnlyList<PostedMovement>>(posted.Error)
            : Result.Success<IReadOnlyList<PostedMovement>>(
            [
                .. posted.Value.Select((receipt, index) =>
                    Describe(receipt, commands[index].LotId, licensePlateId: null))
            ]);
    }

    /// <summary>
    /// Passed straight through to <see cref="StockCounting"/>, and the only method
    /// here that does not build a <see cref="PostingCommand"/>.
    /// <para>
    /// That asymmetry is the honest one. Every other operation on this contract is
    /// a movement wearing a business name, so translating it here is the whole job.
    /// A count is not a movement — most counts post nothing at all — so the work is
    /// reading what the book says, writing the observation and deciding whether
    /// there is anything to post, and none of that is adapter work.
    /// </para>
    /// </summary>
    public Task<Result<CountOutcome>> CountAsync(
        CountInstruction instruction,
        CancellationToken cancellationToken) =>
        counting.CountAsync(instruction, cancellationToken);

    /// <summary>
    /// Destroys stock against the Scrap counterparty.
    /// <para>
    /// The scrap account belongs to the warehouse the goods are leaving, derived
    /// from the origin exactly as the customer account is on a despatch — a
    /// write-off is a despatch to nowhere, and it balances the same way.
    /// </para>
    /// </summary>
    public async Task<Result<PostedMovement>> ScrapAsync(
        ScrapInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instruction.Notes))
        {
            return Result.Failure<PostedMovement>(InventoryErrors.ScrapNotesRequired);
        }

        var origin = await locations.FindAsync(instruction.FromLocationId, cancellationToken);
        if (origin is null) return Result.Failure<PostedMovement>(InventoryErrors.LocationNotFound);

        var scrap = await resolve.CounterpartyAsync(
            origin.WarehouseId, CounterpartyKind.Scrap, cancellationToken);
        if (scrap.IsFailure) return Result.Failure<PostedMovement>(scrap.Error);

        var status = ToStatus(instruction.Condition);

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = instruction.ItemId,
                LotId = instruction.LotId,
                Quantity = instruction.Quantity,
                // The plate is dropped, as it is on a pick. Whatever the pallet was,
                // it is not a pallet any more.
                From = new Holding(instruction.FromLocationId, instruction.LicensePlateId, status),
                To = new Holding(scrap.Value.Id, null, status),
                Type = MovementType.Scrap,
                Reason = instruction.Cause switch
                {
                    ScrapCause.Expired => MovementReason.WriteOffExpired,
                    ScrapCause.Destroyed => MovementReason.Destroyed,
                    _ => MovementReason.WriteOffDamaged
                },
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                Notes = instruction.Notes
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, instruction.LotId, licensePlateId: null);
    }

    /// <summary>
    /// Re-books a quantity into a different condition, in place.
    /// <para>
    /// Both holdings name the same location, which is what
    /// <c>MovementShape</c> insists on for a status change and what makes this a
    /// single conserving entry rather than a loss and a gain. The licence plate
    /// travels, as it does on a putaway: quarantining a pallet does not stop it
    /// being that pallet.
    /// </para>
    /// </summary>
    public async Task<Result<PostedMovement>> ChangeConditionAsync(
        ConditionChangeInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (instruction.FromCondition == instruction.ToCondition)
        {
            return Result.Failure<PostedMovement>(InventoryErrors.ConditionUnchanged);
        }

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = instruction.ItemId,
                LotId = instruction.LotId,
                Quantity = instruction.Quantity,
                From = new Holding(
                    instruction.LocationId, instruction.LicensePlateId, ToStatus(instruction.FromCondition)),
                To = new Holding(
                    instruction.LocationId, instruction.LicensePlateId, ToStatus(instruction.ToCondition)),
                Type = MovementType.StatusChange,
                Reason = ReasonFor(instruction.ToCondition),
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                DecisionTraceId = instruction.DecisionTraceId,
                Notes = instruction.Notes
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, instruction.LotId, instruction.LicensePlateId);
    }

    /// <summary>
    /// Sends goods out to a repair vendor, against InTransit.
    /// <para>
    /// A <see cref="MovementType.Transfer"/> rather than a despatch, and the plate
    /// goes with it: the pallet is still the company's, still expected back, and
    /// still recognisable when it arrives. A despatch would settle it against a
    /// customer who never bought it.
    /// </para>
    /// </summary>
    public async Task<Result<PostedMovement>> SendForRepairAsync(
        RepairInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instruction.Notes))
        {
            return Result.Failure<PostedMovement>(InventoryErrors.RepairNotesRequired);
        }

        var origin = await locations.FindAsync(instruction.FromLocationId, cancellationToken);
        if (origin is null) return Result.Failure<PostedMovement>(InventoryErrors.LocationNotFound);

        var transit = await resolve.CounterpartyAsync(
            origin.WarehouseId, CounterpartyKind.InTransit, cancellationToken);
        if (transit.IsFailure) return Result.Failure<PostedMovement>(transit.Error);

        var status = ToStatus(instruction.Condition);

        var posted = await ledger.PostAsync(
            new PostingCommand
            {
                ItemId = instruction.ItemId,
                LotId = instruction.LotId,
                Quantity = instruction.Quantity,
                From = new Holding(instruction.FromLocationId, instruction.LicensePlateId, status),
                To = new Holding(transit.Value.Id, instruction.LicensePlateId, status),
                Type = MovementType.Transfer,
                Reason = MovementReason.SentForRepair,
                IdempotencyKey = instruction.IdempotencyKey,
                Actor = ToActor(instruction.Actor),
                DecisionTraceId = instruction.DecisionTraceId,
                Notes = instruction.Notes
            },
            cancellationToken);

        return posted.IsFailure
            ? Result.Failure<PostedMovement>(posted.Error)
            : Describe(posted.Value, instruction.LotId, instruction.LicensePlateId);
    }

    /// <summary>
    /// The ledger's own word for a condition change.
    /// <para>
    /// Read off the destination rather than the source, because that is the
    /// direction the fact runs: stock becoming quarantined is a hold being applied
    /// whatever it was before, and stock becoming available is a hold being
    /// released. The one exception is <see cref="StockCondition.Damaged"/>, which is
    /// not a hold at all — it is somebody finding damage, and the ledger has said so
    /// since Phase 1.
    /// </para>
    /// </summary>
    private static MovementReason ReasonFor(StockCondition to) => to switch
    {
        StockCondition.Damaged => MovementReason.DamageFound,
        StockCondition.Expired => MovementReason.ExpiryReached,
        StockCondition.Quarantine or StockCondition.OnHold => MovementReason.QualityHoldApplied,
        StockCondition.Available => MovementReason.QualityHoldReleased,
        _ => MovementReason.ManualCorrection
    };

    private static PostedMovement Describe(MovementReceipt receipt, Guid? lotId, Guid? licensePlateId) =>
        new(
            receipt.MovementId,
            receipt.Sequence,
            receipt.OccurredAt,
            receipt.Quantity,
            lotId,
            licensePlateId,
            receipt.FromQuantityAfter,
            receipt.ToQuantityAfter,
            receipt.WasAlreadyPosted);

    private static LedgerActor ToActor(PostingActor actor) => new(actor.UserId, actor.UserName);

    /// <summary>
    /// The published condition and the internal status are deliberately separate
    /// enums with identical members. They are allowed to diverge — a disposition
    /// the ledger tracks that no other module should be able to book stock into
    /// would exist here and not there — and the mapping is where that stays
    /// visible.
    /// </summary>
    private static InventoryStatus ToStatus(StockCondition condition) => condition switch
    {
        StockCondition.Quarantine => InventoryStatus.Quarantine,
        StockCondition.Damaged => InventoryStatus.Damaged,
        StockCondition.OnHold => InventoryStatus.OnHold,
        StockCondition.Expired => InventoryStatus.Expired,
        _ => InventoryStatus.Available
    };
}
