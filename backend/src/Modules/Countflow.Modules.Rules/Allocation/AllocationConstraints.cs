using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Allocation;

/// <summary>
/// The hard constraints on which stock may satisfy an order line.
/// <para>
/// Written the same way the putaway constraints are — one class, one code, one
/// reason — because the value of that shape is not that putaway needed it. "Why
/// not the pallet in A-07?" has to have a one-word answer whether the question is
/// about where a pallet went or where it came from, and a supervisor arguing with
/// an allocation wants to disable or reorder exactly one rule, not read a
/// hand-written <c>if</c> chain.
/// </para>
/// </summary>
internal static class AllocationConstraints
{
    /// <summary>
    /// There has to be something left after everybody else's claims.
    /// <para>
    /// First, because it is the cheapest check and the most common exclusion in a
    /// busy warehouse — and because "committed to another order" is a different
    /// conversation from every other reason on this list. It is also the rule that
    /// makes the on-hand/available distinction visible to the person reading the
    /// trace, which is the only place most people will ever meet it.
    /// </para>
    /// </summary>
    internal sealed class StockAvailable : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.stock_available";

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context) =>
            candidate.AvailableQuantity > 0m
                ? null
                : RuleNote.Of(
                    "rules.allocation.fully_committed",
                    ("onHand", Format(candidate.OnHandQuantity)),
                    ("reserved", Format(candidate.ReservedQuantity)));
    }

    /// <summary>
    /// Only Available stock is sellable.
    /// <para>
    /// Quarantined and damaged holdings are offered to the rule set rather than
    /// filtered out of the query, so the trace can say the pallet was seen and why
    /// it was passed over. "There are forty cases in quarantine" is very often the
    /// answer the person chasing a short order actually needed.
    /// </para>
    /// </summary>
    internal sealed class ConditionAllocatable : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.condition_allocatable";

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context) =>
            candidate.Condition == StockCondition.Available
                ? null
                : RuleNote.Of("rules.allocation.not_allocatable", ("condition", candidate.Condition.ToString()));
    }

    /// <summary>
    /// Stock has to be somewhere a picker can be sent.
    /// <para>
    /// A pallet still standing on the receiving dock is real, countable and
    /// entirely unpickable: it has not been put away, nobody knows it is there,
    /// and sending a picker to a dock door that a truck is unloading into is how
    /// people get hurt. Roles are a parameter so a warehouse that genuinely picks
    /// off staging can say so without a deploy.
    /// </para>
    /// </summary>
    internal sealed class PickableLocation(IReadOnlyCollection<StorageRole> roles)
        : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.pickable_location";

        public static readonly IReadOnlyCollection<StorageRole> DefaultRoles =
            [StorageRole.Reserve, StorageRole.Forward];

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context)
        {
            var location = context.LocationOf(candidate);

            if (location is null)
            {
                return RuleNote.Of("rules.allocation.location_unknown");
            }

            if (!location.IsActive)
            {
                return RuleNote.Of("rules.allocation.location_blocked");
            }

            return roles.Contains(location.Role)
                ? null
                : RuleNote.Of("rules.allocation.not_pickable", ("role", location.Role.ToString()));
        }
    }

    /// <summary>
    /// The batch the customer named, when they named one.
    /// <para>
    /// Rare and absolute. A retailer replacing a recalled batch or a pharmacy
    /// matching a prescription does not want the freshest stock, they want
    /// <i>that</i> stock, and a preference could be outvoted by three rules that
    /// think they know better.
    /// </para>
    /// </summary>
    internal sealed class LotMatch : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.lot_match";

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context)
        {
            if (context.RequiredLotId is not { } required)
            {
                return null;
            }

            return candidate.LotId == required
                ? null
                : RuleNote.Of("rules.allocation.wrong_lot", ("lot", candidate.LotNumber ?? string.Empty));
        }
    }

    /// <summary>Expired stock is not short-dated, it is not stock. It leaves as a write-off, never on an order.</summary>
    internal sealed class NotExpired : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.not_expired";

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context) =>
            candidate.ExpiryDate is { } expiry && expiry < context.ShipOn
                ? RuleNote.Of(
                    "rules.allocation.expired",
                    ("expiry", expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                : null;
    }

    /// <summary>
    /// Enough life left on the day it ships for the customer to accept it.
    /// <para>
    /// The rule <c>Item.MinimumRemainingShelfLifePercent</c> was put on the
    /// catalogue for, and until Phase 4 the only column in the system nothing
    /// read. It is a genuine and frequently-missed rule in food and pharma: stock
    /// with 20% of its life left is perfectly good, perfectly sellable, and will
    /// be turned away at the customer's gate under a contract that says 33% — so
    /// it has to be excluded here, where somebody can see why, rather than
    /// discovered on a rejected delivery.
    /// </para>
    /// <para>
    /// Missing data admits. An item with no declared shelf life, or a batch with no
    /// expiry, cannot be measured — and refusing to allocate everything the day
    /// somebody imports a supplier file without dates would stop the warehouse
    /// shipping rather than stop it shipping short-dated goods.
    /// </para>
    /// </summary>
    internal sealed class MinimumRemainingShelfLife : IConstraintRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.minimum_shelf_life";

        public string Code => RuleCode;

        public RuleNote? Exclude(StockHolding candidate, AllocationContext context)
        {
            if (context.Item.MinimumRemainingShelfLifePercent is not { } minimum || minimum <= 0)
            {
                return null;
            }

            if (context.RemainingShelfLifeFraction(candidate) is not { } remaining)
            {
                return null;
            }

            var remainingPercent = Math.Round(remaining * 100m, 0);

            return remainingPercent < minimum
                ? RuleNote.Of(
                    "rules.allocation.short_dated",
                    ("remaining", Format(remainingPercent)),
                    ("required", minimum.ToString(CultureInfo.InvariantCulture)),
                    ("expiry", candidate.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty))
                : null;
        }
    }

    /// <summary>
    /// Three decimals at most, and no trailing zeros — a quantity in a reason an
    /// operator reads is "50", not "50.000". Invariant, because the client
    /// re-formats for the reader's locale and the value has to leave here in one
    /// canonical shape.
    /// </summary>
    private static string Format(decimal value) =>
        Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
