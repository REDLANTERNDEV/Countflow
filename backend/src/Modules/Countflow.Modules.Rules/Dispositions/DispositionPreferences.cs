using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Dispositions;

/// <summary>
/// The soft preferences that rank the dispositions the constraints left standing.
/// <para>
/// Every value here is a <b>fixed number attached to an outcome</b>, or a fraction
/// of a real quantity that means something on its own. Not one of them looks at
/// the other candidates. That is the single most important property of this
/// vocabulary and the reason a four-element field is safe to score at all: a
/// field-relative normaliser stretches whatever spread it is given across the whole
/// of <c>[0, 1]</c>, which over four options would mean the gap between the best
/// and second-best disposition always looked total, on every decision, however
/// close the two really were. This codebase has found that shape of flaw five
/// times over larger fields; over this one it would not be a flaw that appears
/// occasionally, it would be the arithmetic.
/// </para>
/// <para>
/// The precedent is <c>putaway.consolidate_same_item</c>, which has scored 1.0 /
/// 0.8 / 0.35 / 0.0 against absolute categories since Phase 3 and has never been
/// one of the five. The lesson generalises: <b>score against a scale that exists
/// without the field.</b>
/// </para>
/// </summary>
internal static class DispositionPreferences
{
    /// <summary>
    /// Get as much of the value back as possible.
    /// <para>
    /// The heaviest preference, and the one that expresses what a returns desk is
    /// for. A unit put back on a shelf is worth what it was worth; one repaired is
    /// worth that less the repair; one held is worth something once somebody looks
    /// at it; one destroyed is worth nothing and cost the freight both ways.
    /// </para>
    /// <para>
    /// Scrap scores zero rather than something small, deliberately. There is no
    /// value recovered by destroying stock, and a rule that pretended otherwise —
    /// "at least it frees the space" — would be smuggling
    /// <see cref="HandlingCost"/>'s judgement into this one and counting it twice.
    /// </para>
    /// </summary>
    internal sealed class RecoverValue(decimal weight)
        : IPreferenceRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.recover_value";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(DispositionOption candidate, DispositionContext context) =>
            candidate.Disposition switch
            {
                Disposition.Restock => ScoreContribution.Of(
                    1m, "rules.disposition.value_fully_recovered", ("sku", context.Item.Sku)),

                Disposition.Repair => ScoreContribution.Of(0.7m, "rules.disposition.value_recovered_after_repair"),

                Disposition.Quarantine => ScoreContribution.Of(0.3m, "rules.disposition.value_undecided"),

                _ => ScoreContribution.Of(0m, "rules.disposition.value_lost")
            };
    }

    /// <summary>
    /// Prefer the outcome that costs the least to carry out.
    /// <para>
    /// The counterweight, and it is meant to pull the other way. Restocking a
    /// return is a condition change, a putaway decision, a task and somebody
    /// walking to a rack; a repair is all of that plus a vendor, a despatch and a
    /// receipt weeks later; a write-off is one movement and a skip. Left alone this
    /// rule would destroy everything, which is exactly why
    /// <c>disposition.scrap_is_not_for_saleable_stock</c> is a constraint and why
    /// this carries less weight than <see cref="RecoverValue"/>.
    /// </para>
    /// <para>
    /// Quarantine scores well and should. Holding something is genuinely cheap
    /// <i>today</i>, and the cost of a hold is that somebody has to come back to
    /// it — a cost this decision does not pay and cannot see. That is a real
    /// limitation of scoring one return at a time, and the honest place for it is
    /// this comment rather than a fudged number.
    /// </para>
    /// </summary>
    internal sealed class HandlingCost(decimal weight)
        : IPreferenceRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.handling_cost";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(DispositionOption candidate, DispositionContext context) =>
            candidate.Disposition switch
            {
                Disposition.Scrap => ScoreContribution.Of(1m, "rules.disposition.handling_trivial"),

                Disposition.Quarantine => ScoreContribution.Of(0.7m, "rules.disposition.handling_deferred"),

                Disposition.Restock => ScoreContribution.Of(
                    0.5m, "rules.disposition.handling_putaway", ("units", Format(context.Quantity))),

                _ => ScoreContribution.Of(0m, "rules.disposition.handling_vendor_round_trip")
            };
    }

    /// <summary>
    /// Believe a return that came straight back; doubt one that took three months.
    /// <para>
    /// The rule that reads the <i>claim</i> rather than the goods, and the only
    /// input to this decision that nobody in the building can see by looking at the
    /// pallet. Something back within days is almost always a customer who ordered
    /// the wrong size; something back after a season has been used, stored badly, or
    /// both, and the carton will not say which.
    /// </para>
    /// <para>
    /// It scores restock on freshness and everything else on its inverse, so the
    /// same fact moves the whole field at once rather than only nudging one option.
    /// A stale return does not merely become less restockable — it becomes more
    /// worth holding and looking at, which is the judgement a returns clerk actually
    /// makes.
    /// </para>
    /// </summary>
    internal sealed class ReturnFreshness(decimal weight, int windowDays)
        : IPreferenceRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.return_freshness";

        public const int DefaultWindowDays = 90;

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(DispositionOption candidate, DispositionContext context)
        {
            var freshness = context.Freshness(windowDays);

            var reason = context.DaysSinceDespatch is { } days
                ? RuleNote.Of(
                    "rules.disposition.days_since_despatch",
                    ("days", days.ToString(CultureInfo.InvariantCulture)),
                    ("window", windowDays.ToString(CultureInfo.InvariantCulture)))
                : RuleNote.Of("rules.disposition.no_despatch_on_record");

            return candidate.Disposition == Disposition.Restock
                ? new ScoreContribution(freshness, reason)
                : new ScoreContribution(1m - freshness, reason);
        }
    }

    /// <summary>
    /// How much of the batch's life is left, for the option that puts it back on a
    /// shelf.
    /// <para>
    /// A fraction of the item's <b>own</b> shelf life, never a window across the
    /// candidates — the same statement FEFO makes in allocation, and it is written
    /// down in <c>invariants.md § The rules engine</c> with an instruction not to
    /// "fix" it back to a ranking. Sixty days left on a two-year life is nearly a
    /// full life and something else should decide; sixty days on a three-week life
    /// is a batch that will expire in the rack, and this should win outright. Both
    /// fall out of the fraction with no special case.
    /// </para>
    /// <para>
    /// Only restock is scored on it, because only restock cares. A batch with two
    /// days left is no better or worse to repair, hold or destroy, and giving the
    /// other three a made-up number here would put shelf life into the ranking
    /// twice — once where it means something and once where it does not.
    /// Non-restock options take the midpoint, which is the value that changes no
    /// ordering among themselves.
    /// </para>
    /// </summary>
    internal sealed class ShelfLifeHeadroom(decimal weight)
        : IPreferenceRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.shelf_life_headroom";

        public string Code => RuleCode;

        public decimal Weight => weight;

        public ScoreContribution Score(DispositionOption candidate, DispositionContext context)
        {
            if (candidate.Disposition != Disposition.Restock)
            {
                return ScoreContribution.Of(0.5m, "rules.disposition.shelf_life_not_relevant");
            }

            if (context.ShelfLifeRemaining is not { } remaining)
            {
                // Not expiry-tracked, or no expiry on file. "Cannot tell" scores as
                // no objection rather than as a perfect one: a rule that awarded a
                // full mark for the absence of data would rank every untracked item
                // above every tracked one, which is a statement about the master
                // data and not about the goods.
                return ScoreContribution.Of(0.5m, "rules.disposition.shelf_life_unknown");
            }

            return ScoreContribution.Of(
                remaining,
                "rules.disposition.shelf_life_left",
                ("remaining", Math.Round(remaining * 100m, 1).ToString("0.#", CultureInfo.InvariantCulture)));
        }
    }

    private static string Format(decimal quantity) =>
        quantity.ToString("0.####", CultureInfo.InvariantCulture);
}
