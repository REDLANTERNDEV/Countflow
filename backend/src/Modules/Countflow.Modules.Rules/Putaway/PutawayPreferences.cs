using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Putaway;

/// <summary>
/// The soft preferences that rank admissible locations.
/// <para>
/// Every one returns a value on <c>[0, 1]</c>, which is what makes their weights
/// comparable: travel distance and cube utilisation measure different things in
/// different units, and a weight of 0.25 has to mean the same amount of influence
/// whichever rule carries it. The engine clamps as well, because a preference
/// that quietly returned 3.0 would outvote every other rule in the set and the
/// resulting trace would look entirely reasonable.
/// </para>
/// </summary>
internal static class PutawayPreferences
{
    /// <summary>
    /// Put it where the same item already is.
    /// <para>
    /// The strongest single preference in most warehouses, and the least obvious.
    /// Fragmenting one SKU across nine bins costs a replenishment trip per bin,
    /// makes counting it a scavenger hunt, and is how a warehouse ends up "full"
    /// at sixty per cent utilisation.
    /// </para>
    /// </summary>
    internal sealed class ConsolidateSameItem(decimal weight)
        : IPreferenceRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.consolidate_same_item";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StorageLocationSnapshot candidate, PutawayContext context)
        {
            var occupancy = context.At(candidate.Id);

            if (occupancy is null)
            {
                return ScoreContribution.Of(0.35m, "rules.putaway.location_empty");
            }

            if (occupancy.HoldsSubjectLot)
            {
                return ScoreContribution.Of(
                    1m,
                    "rules.putaway.holds_same_lot",
                    ("sku", context.Item.Sku),
                    ("units", Format(occupancy.SubjectItemUnits)));
            }

            if (occupancy.SubjectItemUnits > 0)
            {
                return ScoreContribution.Of(
                    0.8m,
                    "rules.putaway.holds_same_item",
                    ("sku", context.Item.Sku),
                    ("units", Format(occupancy.SubjectItemUnits)));
            }

            // Occupied by something else. Not disqualifying — mixed reserve bays
            // are normal — but the last thing to reach for.
            return ScoreContribution.Of(
                0m, "rules.putaway.holds_other_items", ("held", occupancy.DistinctItems.ToString()));
        }
    }

    /// <summary>
    /// Fast movers to the forward pick face, slow movers to reserve.
    /// <para>
    /// The ABC classification is maintained for exactly two purposes and this is
    /// one of them. An A item in a reserve bay costs a replenishment on every
    /// order; a C item occupying a pick face costs the pick face.
    /// </para>
    /// </summary>
    internal sealed class VelocitySlotting(decimal weight)
        : IPreferenceRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.velocity_slotting";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StorageLocationSnapshot candidate, PutawayContext context)
        {
            var value = (context.Item.AbcBand, candidate.Role) switch
            {
                (AbcBand.A, StorageRole.Forward) => 1m,
                (AbcBand.A, _) => 0.35m,
                (AbcBand.B, StorageRole.Forward) => 0.7m,
                (AbcBand.B, _) => 0.7m,
                (AbcBand.C, StorageRole.Forward) => 0.2m,
                _ => 1m
            };

            return ScoreContribution.Of(
                value,
                "rules.putaway.velocity_slotting",
                ("abc", context.Item.AbcBand.ToString()),
                ("role", candidate.Role.ToString()));
        }
    }

    /// <summary>
    /// Prefer the short walk.
    /// <para>
    /// Travel is over half of warehouse labour, and putaway travel is paid twice —
    /// once carrying the pallet out and once on every pick that follows. Scored
    /// from the front of the route rather than from the dock the goods are on,
    /// because a bin's position is a property of the bin and a rule that depended
    /// on which door the truck happened to use would rank the same warehouse
    /// differently every morning.
    /// </para>
    /// </summary>
    internal sealed class TravelDistance(decimal weight)
        : IPreferenceRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.travel_distance";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StorageLocationSnapshot candidate, PutawayContext context)
        {
            var distance = context.NormalisedDistance(candidate);

            return new ScoreContribution(
                1m - distance,
                RuleNote.Of(
                    "rules.putaway.travel_distance",
                    ("zone", candidate.ZoneCode ?? string.Empty),
                    ("percent", Format(Math.Round(distance * 100m, 0)))));
        }
    }

    /// <summary>
    /// Prefer the bin the pallet nearly fills.
    /// <para>
    /// Left to itself, consolidation and travel both push work towards a handful
    /// of big empty bays near the front. This is the counterweight: a slot that a
    /// pallet leaves almost full is a slot that has not been half-wasted. Scores a
    /// flat middling value when the cube is unknown, so missing master data
    /// neither helps nor hurts a candidate.
    /// </para>
    /// </summary>
    internal sealed class CubeUtilisation(decimal weight)
        : IPreferenceRule<StorageLocationSnapshot, PutawayContext>
    {
        public const string RuleCode = "putaway.cube_utilisation";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StorageLocationSnapshot candidate, PutawayContext context)
        {
            if (candidate.MaxVolumeM3 is not { } capacity
                || capacity <= 0m
                || context.RequiredVolumeM3 is not { } required)
            {
                return ScoreContribution.Of(0.5m, "rules.putaway.cube_unknown");
            }

            var free = capacity - (context.At(candidate.Id)?.OccupiedVolumeM3 ?? 0m);
            if (free <= 0m)
            {
                return ScoreContribution.Of(0m, "rules.putaway.cube_full");
            }

            var fill = required / free;

            return new ScoreContribution(
                fill,
                RuleNote.Of("rules.putaway.cube_fill", ("percent", Format(Math.Round(fill * 100m, 0)))));
        }
    }

    private static string Format(decimal value) =>
        Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
}
