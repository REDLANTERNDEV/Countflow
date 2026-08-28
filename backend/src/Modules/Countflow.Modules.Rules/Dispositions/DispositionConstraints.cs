using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Dispositions;

/// <summary>
/// The hard constraints on what may become of a returned unit.
/// <para>
/// These carry more of this decision than the constraints of any other vocabulary
/// carry of theirs, and that is the honest shape rather than an imbalance to fix.
/// Putaway has two thousand candidates and needs preferences to tell good bins
/// from slightly better ones; disposition has four, and most returns have exactly
/// one defensible answer — a sealed, in-date carton goes back on the shelf, and a
/// crushed one does not. The preferences below decide the genuinely open cases,
/// which are the minority, and the constraints say why the rest were never open.
/// </para>
/// <para>
/// Every one of them names a <i>candidate</i> disposition and asks whether this
/// return may have it. That is why they read as four unrelated rules rather than as
/// a chain: the engine offers each of the four options to each of the constraints,
/// and each constraint has an opinion about one of them and nothing to say about
/// the other three.
/// </para>
/// </summary>
internal static class DispositionConstraints
{
    /// <summary>
    /// Nothing goes back into saleable stock unless a person said it was saleable.
    /// <para>
    /// First, because it is the cheapest and the most complete: "the carton was
    /// opened" ends the conversation about restocking, where a shelf-life argument
    /// invites the next question. It is also the constraint that most needs to be a
    /// constraint rather than a heavy preference — a warehouse that let a strong
    /// enough score put opened goods back on a pick face would eventually ship one
    /// to the next customer, and that is not a ranking mistake, it is a complaint.
    /// </para>
    /// </summary>
    internal sealed class RestockNeedsSaleableStock : IConstraintRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.restock_needs_saleable_stock";

        public string Code => RuleCode;

        public RuleNote? Exclude(DispositionOption candidate, DispositionContext context)
        {
            if (candidate.Disposition != Disposition.Restock)
            {
                return null;
            }

            if (context.Findings.Condition != StockCondition.Available)
            {
                return RuleNote.Of(
                    "rules.disposition.not_good_stock",
                    ("condition", context.Findings.Condition.ToString()));
            }

            return context.Findings.IsSealed
                ? null
                : RuleNote.Of("rules.disposition.packaging_opened");
        }
    }

    /// <summary>
    /// Enough life left in it to be worth selling.
    /// <para>
    /// The item's own <c>MinimumRemainingShelfLifePercent</c> is the threshold when
    /// it has one, so a return is held to the same bar as an outbound allocation —
    /// two different rule sets disagreeing about what "too short-dated to ship"
    /// means is how a warehouse restocks a batch it will then refuse to allocate,
    /// and counts it as good stock until it expires on the shelf.
    /// </para>
    /// <para>
    /// An item with no shelf-life data admits the candidate. "Cannot tell" is not
    /// "none left", and the same reasoning governs putaway's capacity rules.
    /// </para>
    /// </summary>
    internal sealed class ShelfLifeRemaining : IConstraintRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.shelf_life_remaining";

        public string Code => RuleCode;

        public RuleNote? Exclude(DispositionOption candidate, DispositionContext context)
        {
            if (candidate.Disposition != Disposition.Restock)
            {
                return null;
            }

            if (context.ShelfLifeRemaining is not { } remaining)
            {
                return null;
            }

            var required = (context.Item.MinimumRemainingShelfLifePercent ?? 0) / 100m;

            return remaining >= required
                ? null
                : RuleNote.Of(
                    "rules.disposition.shelf_life_too_short",
                    ("remaining", Percent(remaining)),
                    ("required", Percent(required)));
        }
    }

    /// <summary>
    /// Repair needs somewhere to send it and something worth sending.
    /// <para>
    /// Two failures under one rule because they are one question — "can this
    /// actually be repaired?" — arrived at from the warehouse's side and the goods'
    /// side, and an operator reading the trace wants the answer, not the taxonomy.
    /// They return different notes, so the trace still distinguishes a warehouse
    /// with no repair vendor from a pallet nobody can fix.
    /// </para>
    /// </summary>
    internal sealed class RepairNeedsARoute : IConstraintRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.repair_needs_a_route";

        public string Code => RuleCode;

        public RuleNote? Exclude(DispositionOption candidate, DispositionContext context)
        {
            if (candidate.Disposition != Disposition.Repair)
            {
                return null;
            }

            if (!context.RepairRouteExists)
            {
                return RuleNote.Of("rules.disposition.no_repair_route");
            }

            return context.Findings.IsRepairable
                ? null
                : RuleNote.Of("rules.disposition.not_repairable");
        }
    }

    /// <summary>
    /// Do not destroy stock somebody could still sell.
    /// <para>
    /// The safety catch on the one irreversible option, and the only constraint in
    /// this vocabulary that exists to protect the decision from its own preferences
    /// rather than to describe the world. Scrapping is operationally the cheapest
    /// thing a warehouse can do with an awkward return — no putaway, no vendor, no
    /// hold to review — so a weight set badly enough would eventually rank it top
    /// for a sealed, in-date carton. Every other mistake this vocabulary can make
    /// is recoverable by re-running the decision; this one is a pallet in a skip.
    /// </para>
    /// <para>
    /// Deliberately narrow. It refuses scrap only for goods that are genuinely
    /// resaleable, which is the same bar
    /// <see cref="RestockNeedsSaleableStock"/> applies — so a supervisor who really
    /// does want to destroy good stock still can, through the ordinary write-off
    /// with their name and their reason on it, which is where that decision belongs.
    /// </para>
    /// </summary>
    internal sealed class ScrapIsNotForSaleableStock : IConstraintRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.scrap_is_not_for_saleable_stock";

        public string Code => RuleCode;

        public RuleNote? Exclude(DispositionOption candidate, DispositionContext context) =>
            candidate.Disposition == Disposition.Scrap && context.IsSaleable
                ? RuleNote.Of("rules.disposition.still_saleable", ("sku", context.Item.Sku))
                : null;
    }

    /// <summary>
    /// A hazardous item that has left the building does not come back onto a pick
    /// face.
    /// <para>
    /// Off by default, and on in any warehouse that handles regulated goods. Where
    /// it applies it is absolute: the chain of custody broke the moment the pallet
    /// went out of the door, and no inspection performed on the receiving dock can
    /// mend it. That is a compliance fact rather than a judgement, which is why it
    /// is a constraint and not a very heavy preference.
    /// </para>
    /// </summary>
    internal sealed class HazardousNeverRestocked : IConstraintRule<DispositionOption, DispositionContext>
    {
        public const string RuleCode = "disposition.hazardous_never_restocked";

        public string Code => RuleCode;

        public RuleNote? Exclude(DispositionOption candidate, DispositionContext context) =>
            candidate.Disposition == Disposition.Restock && context.Item.IsHazardous
                ? RuleNote.Of("rules.disposition.hazardous_return", ("sku", context.Item.Sku))
                : null;
    }

    private static string Percent(decimal fraction) =>
        Math.Round(fraction * 100m, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
