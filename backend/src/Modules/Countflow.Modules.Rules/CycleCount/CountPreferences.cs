using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.CycleCount;

/// <summary>
/// How long each velocity band's record is trusted before it is worth checking
/// again.
/// <para>
/// The classic ABC counting policy, and the reason the classification is
/// maintained at all: an A-class item is touched a hundred times more often than a
/// C, so its record decays a hundred times faster and deserves to be re-verified
/// far sooner. Parameters rather than constants, because these three numbers are
/// the ones a warehouse will actually argue about.
/// </para>
/// </summary>
public sealed record CountIntervals(int ADays = 30, int BDays = 90, int CDays = 180)
{
    public static readonly CountIntervals Default = new();

    public int For(AbcBand band) => band switch
    {
        AbcBand.A => ADays,
        AbcBand.B => BDays,
        _ => CDays
    };
}

/// <summary>
/// The soft preferences that rank what is worth counting.
/// <para>
/// All four return <c>[0, 1]</c>, as the other two vocabularies do, and the reason
/// is the same one stated more sharply here than anywhere: these measure days, a
/// velocity band, a fraction of a SKU's stock and a walk, and a supervisor tuning
/// them has to be able to say "being overdue matters twice as much as being close
/// by" without knowing which of the four is counted in what.
/// </para>
/// </summary>
internal static class CountPreferences
{
    /// <summary>
    /// How far past due the holding is.
    /// <para>
    /// The rule the whole policy exists to serve, and it carries the most weight for
    /// the same reason FEFO does in allocation: everything else here is a tie-break
    /// on top of "nobody has looked at this in a very long time".
    /// </para>
    /// <para>
    /// <b>It scores staleness against the item's own target interval, not against
    /// the other candidates, and that is deliberate twice over.</b> It makes the
    /// score mean the same thing for a fast and a slow mover — 100% of the way to
    /// due is due, whether due is thirty days or a hundred and eighty — which is
    /// what lets one weight govern the whole catalogue. And it cannot be distorted
    /// by stock that is not in the running: a window stretched between the freshest
    /// and stalest candidate is anchored by every holding the constraints go on to
    /// refuse, so one bay untouched since the warehouse opened would flatten the
    /// difference between everything that could actually be counted this week. That
    /// is precisely the failure Phase 4 found in FEFO and fixed; see the note on
    /// <c>AllocationContext.RotationScore</c> before changing it back.
    /// </para>
    /// <para>
    /// Never counted scores 1 outright. There is no fraction to take of an interval
    /// that has never started, and a holding nobody has ever verified is as overdue
    /// as a holding can be.
    /// </para>
    /// </summary>
    internal sealed class Overdue(decimal weight, CountIntervals intervals)
        : IPreferenceRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.overdue";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(CountCandidate candidate, CountContext context)
        {
            if (context.DaysSinceCount(candidate) is not { } days)
            {
                return ScoreContribution.Of(1m, "rules.count.never_counted");
            }

            var target = intervals.For(context.ItemOf(candidate)?.AbcBand ?? AbcBand.C);

            var fraction = target <= 0 ? 1m : Math.Clamp((decimal)days / target, 0m, 1m);

            return new ScoreContribution(
                fraction,
                RuleNote.Of(
                    "rules.count.days_since_count",
                    ("days", days.ToString(CultureInfo.InvariantCulture)),
                    ("target", target.ToString(CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>
    /// Prefer the stock that gets handled.
    /// <para>
    /// Overlaps with <see cref="Overdue"/> and is not redundant with it. That rule
    /// says how much of the interval has elapsed; this one says how fast the record
    /// decays within it, which is what separates two holdings that are both exactly
    /// half way to due. A C-class pallet nobody has touched since it was put away is
    /// almost certainly still correct at ninety days; an A-class pick face at ninety
    /// days has had a thousand hands in it.
    /// </para>
    /// <para>
    /// C scores 0.15 rather than 0. A slow mover is the <i>least</i> urgent thing to
    /// count and not a thing never to count — and a rule that returned zero would
    /// make this preference's contribution to a whole band of the catalogue
    /// identically nothing, which is how a warehouse discovers at stocktake that it
    /// has not looked at its long tail in three years.
    /// </para>
    /// </summary>
    internal sealed class Velocity(decimal weight)
        : IPreferenceRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.velocity";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(CountCandidate candidate, CountContext context)
        {
            var band = context.ItemOf(candidate)?.AbcBand ?? AbcBand.C;

            var value = band switch
            {
                AbcBand.A => 1m,
                AbcBand.B => 0.5m,
                _ => 0.15m
            };

            return ScoreContribution.Of(value, "rules.count.velocity_band", ("band", band.ToString()));
        }
    }

    /// <summary>
    /// Prefer the holding that would hurt most to be wrong about.
    /// <para>
    /// The nearest thing to a value rule the catalogue can honestly support. Real
    /// cycle-counting policy is driven by money, and this system has no unit cost —
    /// so rather than invent one, this measures the share of the item's stock that
    /// sits in the holding, which is the exposure expressed in the only currency
    /// available. An error on a bin holding a SKU's entire stock is an error on the
    /// whole SKU; the same error on one of forty pallets moves nothing anybody
    /// reports on.
    /// </para>
    /// <para>
    /// Quantity alone would have been the obvious rule and would have been wrong: it
    /// is not comparable across items, so a warehouse counting screws and pallet
    /// trucks would rank every box of screws above every truck. A fraction of the
    /// item's own total is comparable, which is the same argument shelf-life
    /// fractions make in allocation.
    /// </para>
    /// </summary>
    internal sealed class StockAtRisk(decimal weight)
        : IPreferenceRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.stock_at_risk";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(CountCandidate candidate, CountContext context)
        {
            var share = context.ShareOfItemStock(candidate);

            return new ScoreContribution(
                share,
                RuleNote.Of(
                    "rules.count.share_of_item_stock",
                    ("percent", Format(Math.Round(share * 100m, 0))),
                    ("units", Format(candidate.OnHandQuantity))));
        }
    }

    /// <summary>
    /// Prefer the short walk, measured against the building exactly as putaway
    /// measures it.
    /// <para>
    /// The weakest of the four, and included because a count sheet is walked. Its
    /// real effect is to break ties towards the front of the route, so a shift's
    /// worth of counts clusters rather than scattering the counter across the
    /// building — which is worth a little and is not worth outvoting staleness, hence
    /// the weight.
    /// </para>
    /// <para>
    /// <b>It only stays the weakest if the scale is the warehouse.</b> Normalised
    /// across the admissible field instead, two adjacent bays come out at 0% and
    /// 100% along the route and this rule silently becomes the strongest thing in
    /// the set — which is what the container run found it doing. See
    /// <c>CountContext.NormalisedDistance</c> before touching the normaliser.
    /// </para>
    /// <para>
    /// Worth being clear about what this does <i>not</i> do: it does not build an
    /// optimal route, and it does not know which other counts were raised alongside
    /// it. Sequencing a count sheet properly is a routing problem, not a ranking one,
    /// and a preference that tried would be judgement the trace could not explain.
    /// </para>
    /// </summary>
    internal sealed class TravelDistance(decimal weight, TravelScale scale)
        : IPreferenceRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.travel_distance";

        public const TravelScale DefaultScale = TravelScale.Building;

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(CountCandidate candidate, CountContext context)
        {
            var location = context.LocationOf(candidate);

            if (location is null)
            {
                return ScoreContribution.Of(0m, "rules.count.location_unknown");
            }

            var distance = context.NormalisedDistance(location, scale);

            return new ScoreContribution(
                1m - distance,
                RuleNote.Of(
                    "rules.count.travel_distance",
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
