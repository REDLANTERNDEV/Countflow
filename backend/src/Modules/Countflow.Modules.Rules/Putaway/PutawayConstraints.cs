using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// The hard constraints on where a pallet may go.
/// <para>
/// Each one is a separate class with its own code, and that is the point. A
/// candidate is excluded by exactly one named rule, so "why not aisle 7?" has a
/// one-word answer that a supervisor can then argue with, disable, or reorder —
/// rather than the single opaque "no suitable location" a hand-written
/// <c>if</c> chain produces.
/// </para>
/// <para>
/// Every note code here has a matching entry in <c>packages/i18n</c>. The backend
/// never emits the sentence.
/// </para>
/// </summary>
internal static class PutawayConstraints
{
    /// <summary>Blocked racking is excluded from putaway without being deleted — it still holds stock and still has history.</summary>
    internal sealed class LocationActive : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.location_active";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context) =>
            candidate.IsActive ? null : RuleNote.Of("rules.putaway.location_blocked");
    }

    /// <summary>
    /// Docks, pack benches and the bin the goods are standing in are not storage.
    /// <para>
    /// The origin is excluded here rather than filtered out of the candidate list,
    /// so the trace can say the obvious thing out loud: the system did consider
    /// leaving the pallet where it was, and declined.
    /// </para>
    /// </summary>
    internal sealed class StorableLocation(IReadOnlyCollection<StorageRole> roles)
        : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.storable_location";

        public static readonly IReadOnlyCollection<StorageRole> DefaultRoles =
            [StorageRole.Reserve, StorageRole.Forward];

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (candidate.Id == context.OriginLocationId)
            {
                return RuleNote.Of("rules.putaway.is_origin");
            }

            return roles.Contains(candidate.Role)
                ? null
                : RuleNote.Of("rules.putaway.not_storage", ("role", candidate.Role.ToString()));
        }
    }

    /// <summary>
    /// Temperature. The first thing anyone checks and the most expensive to get
    /// wrong: chilled goods in an ambient aisle is spoilage, and it is discovered
    /// long after the operator has gone home.
    /// </summary>
    internal sealed class TemperatureMatch : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.temperature_match";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context) =>
            candidate.Temperature == context.Item.TemperatureBand
                ? null
                : RuleNote.Of(
                    "rules.putaway.temperature_mismatch",
                    ("item", context.Item.TemperatureBand.ToString()),
                    ("location", candidate.Temperature.ToString()));
    }

    /// <summary>
    /// One batch of an item per location.
    /// <para>
    /// Two lots of the same SKU in one bin makes FEFO rotation a matter of which
    /// carton the picker reaches first, and makes a recall a matter of quarantining
    /// the whole bin. Only applies to lot-tracked items, because for everything
    /// else there is no batch to comingle.
    /// </para>
    /// </summary>
    internal sealed class LotPurity : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.lot_purity";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (!context.Item.IsLotTracked)
            {
                return null;
            }

            return context.At(candidate.Id) is { HoldsOtherLotOfSubjectItem: true }
                ? RuleNote.Of("rules.putaway.lot_commingling", ("sku", context.Item.Sku))
                : null;
        }
    }

    /// <summary>
    /// The cap on distinct SKUs. Pick faces are normally 1 — mixing SKUs in one
    /// pick slot is how mis-picks happen, and a mis-pick costs more than the extra
    /// walk to an empty slot.
    /// </summary>
    internal sealed class DistinctItemLimit : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.distinct_item_limit";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (candidate.MaxDistinctItems is not { } limit)
            {
                return null;
            }

            var occupancy = context.At(candidate.Id);
            if (occupancy is null)
            {
                return null;
            }

            // The incoming item only adds to the count if it is not already there,
            // which is what lets a full pick face keep being replenished.
            var after = occupancy.DistinctItems + (occupancy.SubjectItemUnits > 0 ? 0 : 1);

            return after > limit
                ? RuleNote.Of(
                    "rules.putaway.distinct_item_limit",
                    ("limit", limit.ToString()),
                    ("held", occupancy.DistinctItems.ToString()))
                : null;
        }
    }

    /// <summary>
    /// Cube. Unknown capacity admits, unknown requirement admits — a rule that
    /// excluded on missing master data would empty the candidate list of an
    /// entire warehouse on the day somebody imported a supplier file without
    /// dimensions.
    /// </summary>
    internal sealed class VolumeCapacity : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.volume_capacity";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (candidate.MaxVolumeM3 is not { } capacity || context.RequiredVolumeM3 is not { } required)
            {
                return null;
            }

            var used = context.At(candidate.Id)?.OccupiedVolumeM3 ?? 0m;
            var free = capacity - used;

            return required > free
                ? RuleNote.Of(
                    "rules.putaway.insufficient_volume",
                    ("free", Round(free)),
                    ("required", Round(required)))
                : null;
        }
    }

    /// <summary>Weight, on the same terms as volume. A rack that fails is a rack that falls.</summary>
    internal sealed class WeightCapacity : IConstraintRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.weight_capacity";

        public string Code => RuleCode;

        public RuleNote? Exclude(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (candidate.MaxWeightKg is not { } capacity || context.RequiredWeightKg is not { } required)
            {
                return null;
            }

            var used = context.At(candidate.Id)?.OccupiedWeightKg ?? 0m;
            var free = capacity - used;

            return required > free
                ? RuleNote.Of(
                    "rules.putaway.insufficient_weight",
                    ("free", Round(free)),
                    ("required", Round(required)))
                : null;
        }
    }

    /// <summary>
    /// Three decimals, invariant. The host runs with invariant globalisation on
    /// (see Directory.Build.props) and a note argument is a value the client will
    /// re-format for the reader's locale, so it must leave here in one canonical
    /// shape.
    /// </summary>
    private static string Round(decimal value) =>
        Math.Round(value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
