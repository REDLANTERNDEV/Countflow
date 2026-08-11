using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Allocation;

/// <summary>
/// The soft preferences that rank admissible holdings.
/// <para>
/// Every one returns <c>[0, 1]</c>, as putaway's do, and for the reason that
/// matters more here than there: these four measure days, units, a location's job
/// and a walk, and a warehouse tuning them has to be able to say "rotation matters
/// twice as much as travel" without knowing that one is counted in days and the
/// other in metres.
/// </para>
/// </summary>
internal static class AllocationPreferences
{
    /// <summary>
    /// First expired, first out.
    /// <para>
    /// The default rotation rule for anything dated, and the reason expiry is
    /// tracked at all. Not a constraint: the oldest batch is the one that
    /// <i>should</i> go, but sending a picker across the building for four units
    /// when eighty of a batch one day younger are standing at the pick face is a
    /// worse outcome than the rotation is worth.
    /// </para>
    /// <para>
    /// <b>It scores age as a fraction of the item's own shelf life, and that has a
    /// consequence worth stating before somebody "fixes" it.</b> Two batches sixty
    /// days apart on a two-year life score almost the same, so travel or the pick
    /// face will break the tie between them — and on a chilled item with a
    /// three-week life, the same sixty days is the whole difference and rotation
    /// wins by a mile. That is the behaviour a warehouse actually wants out of
    /// FEFO: decisive when the age gap means something, deferential when it does
    /// not. Ranking batches against each other instead would make rotation
    /// decisive even when the difference is meaningless, which is how pickers end
    /// up walking the building to save a day of shelf life on tinned goods.
    /// </para>
    /// </summary>
    internal sealed class FirstExpiredFirstOut(decimal weight)
        : IPreferenceRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.fefo";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StockHolding candidate, AllocationContext context)
        {
            if (candidate.ExpiryDate is not { } expiry)
            {
                // Undated stock neither helps nor hurts. Scoring it 0 would push
                // every non-perishable line to the back of its own field, and
                // scoring it 1 would make undated stock beat a genuinely urgent
                // batch.
                return ScoreContribution.Of(0.5m, "rules.allocation.no_expiry");
            }

            return new ScoreContribution(
                context.RotationScore(candidate),
                RuleNote.Of(
                    "rules.allocation.expires_on",
                    ("expiry", expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("days", (expiry.DayNumber - context.ShipOn.DayNumber).ToString(CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>
    /// Prefer the holding this order empties.
    /// <para>
    /// A part-pallet left in a reserve bay is a bay that cannot take a new pallet
    /// and a holding somebody has to remember. Clearing the remnant first is what
    /// stops a warehouse filling up with almost-empty slots — the same argument as
    /// putaway's cube utilisation, run backwards.
    /// </para>
    /// </summary>
    internal sealed class ClearTheHolding(decimal weight)
        : IPreferenceRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.clear_holding";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StockHolding candidate, AllocationContext context)
        {
            var available = candidate.AvailableQuantity;

            if (available <= 0m)
            {
                return ScoreContribution.Of(0m, "rules.allocation.nothing_free");
            }

            if (available <= context.DemandQuantity)
            {
                return ScoreContribution.Of(
                    1m,
                    "rules.allocation.would_clear",
                    ("units", Format(available)));
            }

            // Bigger than the demand: still useful, and the closer the fit the
            // better, so a holding that leaves twelve behind beats one that leaves
            // nine hundred.
            return new ScoreContribution(
                context.DemandQuantity / available,
                RuleNote.Of(
                    "rules.allocation.would_leave",
                    ("units", Format(available - context.DemandQuantity))));
        }
    }

    /// <summary>
    /// Prefer the pick face.
    /// <para>
    /// A pick from a forward face is a person walking; a pick from reserve is a
    /// truck, a pallet down, a partial back up and a bay out of service while it
    /// happens. The forward face exists precisely so that most picks do not touch
    /// reserve, and an allocator that ignored the distinction would quietly undo
    /// the slotting decisions putaway had made.
    /// </para>
    /// </summary>
    internal sealed class PreferPickFace(decimal weight)
        : IPreferenceRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.prefer_pick_face";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StockHolding candidate, AllocationContext context)
        {
            var location = context.LocationOf(candidate);

            var value = location?.Role switch
            {
                StorageRole.Forward => 1m,
                StorageRole.Staging => 0.5m,
                StorageRole.Reserve => 0.25m,
                _ => 0m
            };

            return ScoreContribution.Of(
                value,
                "rules.allocation.location_role",
                ("role", (location?.Role ?? StorageRole.None).ToString()));
        }
    }

    /// <summary>
    /// Prefer the short walk, measured exactly as putaway measures it.
    /// <para>
    /// Shared deliberately rather than reimplemented: a warehouse where putaway
    /// and picking disagreed about which end of the building is near would produce
    /// two rule sets that quietly fight each other.
    /// </para>
    /// </summary>
    internal sealed class TravelDistance(decimal weight)
        : IPreferenceRule<StockHolding, AllocationContext>
    {
        public const string RuleCode = "allocation.travel_distance";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(StockHolding candidate, AllocationContext context)
        {
            var location = context.LocationOf(candidate);

            if (location is null)
            {
                return ScoreContribution.Of(0m, "rules.allocation.location_unknown");
            }

            var distance = context.NormalisedDistance(location);

            return new ScoreContribution(
                1m - distance,
                RuleNote.Of(
                    "rules.allocation.travel_distance",
                    ("zone", location.ZoneCode ?? string.Empty),
                    ("percent", Format(Math.Round(distance * 100m, 0)))));
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
