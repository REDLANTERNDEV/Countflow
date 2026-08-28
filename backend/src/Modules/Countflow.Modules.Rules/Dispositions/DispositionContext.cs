using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.Dispositions;

/// <summary>
/// Everything the disposition rules are allowed to know, gathered once before
/// evaluation starts.
/// <para>
/// The same discipline as the other three contexts, and the fourth demonstration
/// that it is what makes the engine reusable. What is worth noticing is how little
/// of this is the warehouse. Putaway's context is a building, allocation's is a
/// demand, counting's is a set of timestamps; this one is <b>a person's report of
/// what they found in a carton</b>, plus two facts about whether the building has
/// anywhere to send it. That is the widest the four contexts get, and the engine
/// does not notice.
/// </para>
/// <para>
/// <b>Every scale in here is absolute.</b> None of the preferences measures a
/// candidate against the other candidates, and that is not a stylistic choice — it
/// is the reason a four-element field is safe to rank at all. A field-relative
/// normaliser puts its best candidate at 1 and its worst at 0 however alike they
/// really are, which over two thousand bins is a bug found five times in this
/// codebase and over four fixed options would be a bug on every single decision.
/// A degenerate field — one surviving disposition — scores here exactly what it
/// deserves on its own merits, because there is nothing to be degenerate relative
/// to. See <c>invariants.md § The rules engine</c>.
/// </para>
/// </summary>
/// <param name="RepairRouteExists">
/// Whether this warehouse has an InTransit counterparty to send goods out through.
/// Looked up once here rather than asked of each rule, and offered to the rule set
/// as something to exclude on rather than filtered out of the field — so a
/// supervisor asking why nothing was repaired is told the warehouse has nowhere to
/// send it, which is a configuration answer rather than a silence.
/// </param>
/// <param name="ShelfLifeRemaining">
/// What is left of the batch's life, as a fraction of the item's own shelf life on
/// <c>[0, 1]</c>. Null when the item is not expiry-tracked, or the batch has no
/// expiry on file — which is "cannot tell", not "none left".
/// <para>
/// A fraction of the item's <i>own</i> life rather than a window across the
/// candidates, for exactly the reason FEFO is: sixty days on a two-year life is
/// nearly nothing and something else should decide, while sixty days on a
/// three-week life is everything. Both fall out of the fraction, and neither falls
/// out of a ranking. See <c>invariants.md § The rules engine</c>.
/// </para>
/// </param>
public sealed record DispositionContext(
    ItemSnapshot Item,
    Guid? LotId,
    decimal Quantity,
    Guid AtLocationId,
    InspectionFindings Findings,
    bool RepairRouteExists,
    int? DaysSinceDespatch,
    decimal? ShelfLifeRemaining)
{
    /// <summary>
    /// Whether the goods, as inspected, are fit to sell again.
    /// <para>
    /// Two facts and not one. Condition alone is not enough — an inspector can find
    /// the contents perfectly good inside a carton nobody would accept on a
    /// pallet — and sealed alone is not enough either, since an unopened box can
    /// still be out of date. Both, or it does not go back on a shelf.
    /// </para>
    /// </summary>
    public bool IsSaleable =>
        Findings.Condition == StockCondition.Available && Findings.IsSealed;

    /// <summary>
    /// How fresh the return is, on <c>[0, 1]</c>, where 1 is "came straight back".
    /// <para>
    /// Absolute, against a fixed ninety-day window rather than against the other
    /// returns in front of it, and the window is a rule parameter so a warehouse
    /// selling perishables can shorten it. What it measures is not the goods but
    /// the <i>claim</i>: something back within a week is a customer who ordered the
    /// wrong thing, and something back after three months is a customer who used it
    /// and changed their mind. The first is restockable and the second usually is
    /// not, whatever the carton looks like.
    /// </para>
    /// <para>
    /// A return with no order behind it scores the midpoint rather than either end.
    /// Nobody knows how long they had it, and both "assume it is fresh" and "assume
    /// it is ancient" are inventions the trace would then have to defend.
    /// </para>
    /// </summary>
    public decimal Freshness(int windowDays) =>
        DaysSinceDespatch is not { } days
            ? 0.5m
            : Math.Clamp(1m - ((decimal)days / Math.Max(1, windowDays)), 0m, 1m);
}
