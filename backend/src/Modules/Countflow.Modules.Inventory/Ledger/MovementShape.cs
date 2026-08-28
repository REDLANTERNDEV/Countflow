using Countflow.Modules.Inventory.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Which counterparties each kind of movement is allowed to touch.
/// <para>
/// Without this, the double-entry scheme still balances but stops meaning
/// anything: a "receipt" from the Scrap account and a "despatch" to Supplier both
/// conserve stock perfectly while making the inbound and outbound reports
/// nonsense. The virtual locations are what give a movement its accounting
/// meaning, so the pairing is validated rather than trusted.
/// </para>
/// </summary>
public static class MovementShape
{
    public static Result Validate(MovementType type, LocationSnapshot from, LocationSnapshot to)
    {
        switch (type)
        {
            case MovementType.Receipt:
                // Supplier or Customer, and the second one is Phase 7's correction
                // rather than a loosening. A customer return is the same *shape* —
                // goods entering the building from a counterparty — and shape is
                // what this rule is about; which counterparty it came from is the
                // business fact, and MovementReason already carries it. Until this
                // changed, every return credited Supplier while the despatch that
                // sent the goods out had debited Customer, so the customer contra
                // account grew forever and could never be reconciled. Scrap and
                // Adjustment stay out: stock does not come *back* from having been
                // destroyed, and an inbound correction is an Adjustment by name.
                return Require(
                    from.Counterparty is CounterpartyKind.Supplier or CounterpartyKind.Customer
                    && !to.IsVirtual,
                    "A receipt moves stock from the Supplier or Customer counterparty into a physical location.");

            case MovementType.Despatch:
                return Require(
                    !from.IsVirtual && to.Counterparty == CounterpartyKind.Customer,
                    "A despatch moves stock from a physical location to the Customer counterparty.");

            case MovementType.Scrap:
                return Require(
                    !from.IsVirtual && to.Counterparty == CounterpartyKind.Scrap,
                    "A write-off moves stock from a physical location to the Scrap counterparty.");

            case MovementType.Adjustment:
            case MovementType.CountAdjustment:
                // Exactly one side, so an adjustment is always visibly a gain or a
                // loss against the adjustment account rather than a quiet
                // shuffle between two shelves dressed up as a correction.
                return Require(
                    from.Counterparty == CounterpartyKind.Adjustment ^ to.Counterparty == CounterpartyKind.Adjustment,
                    "An adjustment has the Adjustment counterparty on exactly one side.");

            case MovementType.StatusChange:
                return Require(
                    !from.IsVirtual && !to.IsVirtual && from.Id == to.Id,
                    "A status change keeps stock in the same physical location.");

            case MovementType.Putaway:
            case MovementType.Replenishment:
            case MovementType.Pick:
                return Require(
                    !from.IsVirtual && !to.IsVirtual,
                    "Internal work moves stock between two physical locations.");

            case MovementType.Transfer:
                // InTransit is legitimate on either side: stock leaving for
                // another site is not yet a despatch, and the receiving site's
                // entry comes from the same account.
                return Require(
                    IsPhysicalOrInTransit(from) && IsPhysicalOrInTransit(to),
                    "A transfer moves stock between physical locations, or to and from InTransit.");

            case MovementType.Reversal:
                // No shape rule. A reversal mirrors an entry that was already
                // validated when it was posted; re-deriving the rule for the
                // mirrored direction would reject perfectly good corrections.
                return Result.Success();

            default:
                return Result.Failure(InventoryErrors.InvalidCounterparty(
                    $"Movement type {type} has no defined counterparty rule."));
        }
    }

    private static bool IsPhysicalOrInTransit(LocationSnapshot location) =>
        !location.IsVirtual || location.Counterparty == CounterpartyKind.InTransit;

    private static Result Require(bool satisfied, string detail) => satisfied
        ? Result.Success()
        : Result.Failure(InventoryErrors.InvalidCounterparty(detail));
}
