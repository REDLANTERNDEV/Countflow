namespace Countflow.SharedKernel.Rules;

/// <summary>
/// Codes a rule can be flagged with when what it <i>did</i> to a ranking does not
/// match what the rule set asked it to do.
/// <para>
/// Hints, not verdicts, and worded that way on purpose. Every one of them has a
/// perfectly innocent explanation available — a field really can span the whole
/// building, a rule really can have nothing to say about four identical pallets —
/// so the report names the rule and shows the arithmetic rather than pronouncing
/// on it. What makes them worth having is that all three of the scoring flaws
/// found so far were invisible in a test suite and blindingly obvious in one line
/// of this shape.
/// </para>
/// </summary>
public static class InfluenceFindings
{
    /// <summary>
    /// The rule accounted for far more of the ranking than its weight asked it to,
    /// and it is not the rule the set considers most important.
    /// <para>
    /// This is the shape of every normalisation flaw found so far: a preference
    /// measuring metres against the wrong scale swings the full width of the score
    /// while the rule it is meant to defer to moves by a hundredth.
    /// </para>
    /// </summary>
    public const string OutvotesItsWeight = "influence.outvotes_its_weight";

    /// <summary>
    /// The rule scored every candidate identically, so it did no ranking at all —
    /// its weight was spent on nothing.
    /// <para>
    /// Ordinary and expected for one decision (four pallets of the same item are
    /// the same age). Persistent across a whole replay it means either a rule that
    /// never discriminates in this warehouse, or a normaliser that has collapsed:
    /// a lone candidate measured against its own field is both the best and the
    /// worst and scores a flat 1.
    /// </para>
    /// </summary>
    public const string NeverDiscriminates = "influence.never_discriminates";

    /// <summary>
    /// The rule used the entire <c>[0, 1]</c> range across this field.
    /// <para>
    /// The fingerprint of a field-relative normaliser, which by construction puts
    /// its best candidate at 1 and its worst at 0 however alike the two really
    /// are. Legitimate for a genuinely binary judgement — a location is empty or
    /// it is not — which is why this is reported beside
    /// <see cref="InfluenceLine.DistinctValues"/> rather than on its own.
    /// </para>
    /// </summary>
    public const string UsesTheFullRange = "influence.uses_the_full_range";
}

/// <summary>
/// Where the lines are drawn. Data rather than constants because the honest answer
/// to "how much is too much" is warehouse-specific, and because a test that wants
/// to assert on the raw arithmetic should not have to fight a threshold.
/// </summary>
/// <param name="DominanceFactor">
/// How many times its nominal share a rule must realise before it is flagged.
/// </param>
/// <param name="DominanceFloor">
/// The share below which dominance is not interesting. Without it, a decision
/// where every rule but one is silent flags that one every time, which is true and
/// useless.
/// </param>
public sealed record InfluenceThresholds(
    decimal DominanceFactor = 2m,
    decimal DominanceFloor = 0.4m,
    decimal FullRange = 0.99m)
{
    public static readonly InfluenceThresholds Default = new();
}

/// <summary>
/// What one preference rule actually did to one ranking.
/// </summary>
/// <param name="RealisedSwing">
/// <c>Weight × (highest value − lowest value)</c>: how many points of score this
/// rule put between the best candidate and the worst. The number that matters, and
/// the one a weight on its own cannot tell you — a rule weighted 0.45 that scores
/// every candidate 0.6 moved nothing.
/// </param>
/// <param name="RealisedShare">
/// This rule's swing as a fraction of every rule's swing. What fraction of the
/// ranking it is responsible for.
/// </param>
/// <param name="NominalShare">
/// This rule's weight as a fraction of the set's weight. What fraction of the
/// ranking it was <i>asked</i> to be responsible for.
/// </param>
/// <param name="MarginContribution">
/// How much of the winner's lead over the runner-up came from this rule. Signed:
/// negative means the winner was behind on this rule and won in spite of it.
/// <para>
/// The most legible number in the report, because it answers the question a
/// supervisor actually asks. "It beat the second bay by 0.136, and 0.136 of that
/// was travel" is a complete explanation; "travel is weighted 0.15" is not.
/// </para>
/// </param>
/// <param name="DistinctValues">
/// How many different scores this rule handed out. Two is a judgement with two
/// answers; twenty is a graded measurement. It is what separates a legitimately
/// binary rule from a continuous one whose scale has collapsed onto its field.
/// </param>
public sealed record InfluenceLine(
    string RuleCode,
    decimal Weight,
    decimal LowestValue,
    decimal HighestValue,
    decimal RealisedSwing,
    decimal RealisedShare,
    decimal NominalShare,
    decimal MarginContribution,
    int DistinctValues,
    IReadOnlyList<string> Findings)
{
    public decimal ValueSpread => HighestValue - LowestValue;
}

/// <summary>
/// What did the deciding.
/// </summary>
/// <param name="WinningMargin">
/// The winner's score less the runner-up's. Zero when there was no contest — one
/// admissible candidate, or a tie broken on the key.
/// </param>
/// <param name="Rules">Ordered by realised swing, so the rule that decided is first.</param>
public sealed record InfluenceReport(
    int AdmissibleCount,
    decimal WinningMargin,
    IReadOnlyList<InfluenceLine> Rules)
{
    public static readonly InfluenceReport Empty = new(0, 0m, []);

    public IEnumerable<InfluenceLine> Flagged => Rules.Where(rule => rule.Findings.Count > 0);
}

/// <summary>
/// Reads a ranking back and reports which rules produced it.
/// <para>
/// <b>Why this exists.</b> A rule set states its intent as weights, and a weight is
/// a claim about a rule's importance that nothing checks. What a rule is
/// <i>actually</i> worth to a ranking is its weight multiplied by how much its
/// score varies across the candidates in front of it — and that second factor is
/// invisible until there is a real warehouse behind the decision. Three scoring
/// flaws have now been found by driving a phase through the containers and none by
/// the test suite, and all three were the same failure: a preference normalised
/// against the wrong scale, swinging the full width of the score over candidates
/// that were nearly identical, quietly outvoting the rule it was meant to defer
/// to. Every one of them would have been one line of this report.
/// </para>
/// <para>
/// Pure and stateless, like the engine it reads. It needs no database, so it runs
/// over a decision as it is made, over a trace stored last quarter, and over a
/// replay of a candidate rule set against a year of history, and says the same
/// thing in all three places.
/// </para>
/// <para>
/// It reports only on preferences. A constraint has no score to vary — it excluded
/// the candidate or it did not, and the trace already names which one did.
/// </para>
/// </summary>
public static class RuleInfluence
{
    /// <summary>
    /// The report for a decision that has just been made.
    /// </summary>
    public static InfluenceReport Of<TCandidate>(
        Decision<TCandidate> decision,
        InfluenceThresholds? thresholds = null)
        where TCandidate : IRuleCandidate =>
        Of(
            [
                .. decision.Candidates
                    .Where(candidate => candidate.IsAdmissible)
                    .OrderBy(candidate => candidate.Rank)
                    .Select(candidate => new ScoredCandidate(candidate.Score, candidate.Scores))
            ],
            thresholds);

    /// <summary>
    /// The report for a ranking read back from storage.
    /// <para>
    /// The stored half of a trace keeps every admissible candidate and its full
    /// scored breakdown — it is only the <i>losing</i> field that retention trims —
    /// so a trace written months ago answers this exactly. That is what makes the
    /// diagnostic retrospective rather than something you have to have thought to
    /// switch on.
    /// </para>
    /// </summary>
    /// <param name="ranked">Admissible candidates, best first.</param>
    public static InfluenceReport Of(
        IReadOnlyList<ScoredCandidate> ranked,
        InfluenceThresholds? thresholds = null)
    {
        var limits = thresholds ?? InfluenceThresholds.Default;

        if (ranked.Count == 0)
        {
            return InfluenceReport.Empty;
        }

        // Grouped by code rather than zipped by position. The engine hands every
        // candidate the same preferences in the same order, so an index would work
        // today — but a trace is read back by code, and a report that silently
        // mislabelled two rules because a set was edited between writing and
        // reading would be worse than no report.
        var byCode = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        var weights = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var candidate in ranked)
        {
            foreach (var score in candidate.Scores)
            {
                if (!byCode.TryGetValue(score.RuleCode, out var values))
                {
                    values = [];
                    byCode[score.RuleCode] = values;
                    weights[score.RuleCode] = score.Weight;
                    order.Add(score.RuleCode);
                }

                values.Add(score.Value);
            }
        }

        if (order.Count == 0)
        {
            return new InfluenceReport(ranked.Count, Margin(ranked), []);
        }

        var swings = order.ToDictionary(
            code => code,
            code => weights[code] * (byCode[code].Max() - byCode[code].Min()),
            StringComparer.Ordinal);

        var totalSwing = swings.Values.Sum();
        var totalWeight = weights.Values.Sum();
        var heaviest = weights.Values.Max();

        var lines = new List<InfluenceLine>(order.Count);

        foreach (var code in order)
        {
            var values = byCode[code];
            var weight = weights[code];
            var swing = swings[code];

            var realisedShare = totalSwing <= 0m ? 0m : swing / totalSwing;
            var nominalShare = totalWeight <= 0m ? 0m : weight / totalWeight;
            var spread = values.Max() - values.Min();
            var distinct = values.Distinct().Count();

            var findings = new List<string>();

            // Two candidates at minimum. One candidate has no ranking to influence,
            // and every rule would be flagged as discriminating nothing — which is
            // true, uninteresting, and would bury the cases that matter.
            if (ranked.Count > 1)
            {
                if (weight > 0m && spread == 0m)
                {
                    findings.Add(InfluenceFindings.NeverDiscriminates);
                }

                if (spread >= limits.FullRange)
                {
                    findings.Add(InfluenceFindings.UsesTheFullRange);
                }

                if (realisedShare >= limits.DominanceFloor
                    && realisedShare >= limits.DominanceFactor * nominalShare
                    && weight < heaviest)
                {
                    findings.Add(InfluenceFindings.OutvotesItsWeight);
                }
            }

            lines.Add(new InfluenceLine(
                code,
                weight,
                values.Min(),
                values.Max(),
                swing,
                realisedShare,
                nominalShare,
                MarginContribution(ranked, code, weight),
                distinct,
                findings));
        }

        return new InfluenceReport(
            ranked.Count,
            Margin(ranked),
            [.. lines.OrderByDescending(line => line.RealisedSwing).ThenBy(line => line.RuleCode, StringComparer.Ordinal)]);
    }

    private static decimal Margin(IReadOnlyList<ScoredCandidate> ranked) =>
        ranked.Count < 2 ? 0m : ranked[0].Score - ranked[1].Score;

    /// <summary>
    /// How much of the winner's lead this rule is responsible for. Signed, because
    /// "it won despite being the worse choice on rotation" is exactly the sentence
    /// somebody reviewing a rule change needs to be able to read.
    /// </summary>
    private static decimal MarginContribution(
        IReadOnlyList<ScoredCandidate> ranked,
        string code,
        decimal weight)
    {
        if (ranked.Count < 2)
        {
            return 0m;
        }

        var winner = ranked[0].Scores.FirstOrDefault(score => score.RuleCode == code);
        var runnerUp = ranked[1].Scores.FirstOrDefault(score => score.RuleCode == code);

        if (winner is null || runnerUp is null)
        {
            return 0m;
        }

        return weight * (winner.Value - runnerUp.Value);
    }
}

/// <summary>
/// One admissible candidate as the influence report needs it: its total, and the
/// weighted breakdown behind it.
/// <para>
/// Deliberately not <c>CandidateEvaluation&lt;T&gt;</c>. That record is generic in
/// the candidate type, and the whole point of this diagnostic is that it reads a
/// putaway, an allocation and a cycle count the same way — including ones read back
/// out of a table, where the candidate type is long gone and only its key survives.
/// </para>
/// </summary>
public sealed record ScoredCandidate(decimal Score, IReadOnlyList<ScoreLine> Scores);
