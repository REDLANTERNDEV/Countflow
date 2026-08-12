using Countflow.SharedKernel.Rules;

namespace Countflow.SharedKernel.Tests.Rules;

/// <summary>
/// The diagnostic that would have caught all three of the scoring flaws found so
/// far, tested on the arithmetic rather than on a warehouse.
/// <para>
/// The warehouse half is <c>ReplayTests</c>, and the division is the point. What
/// can be checked here is that the numbers mean what they say: that a rule's
/// realised swing is its weight times how much it actually varied, that a weight on
/// its own predicts nothing, and that a rule doing most of the deciding on a small
/// weight is flagged. What cannot be checked here — and what three phases of
/// container runs kept discovering — is which rules do that in a real candidate
/// field, because a hand-built field is exactly where a broken normaliser and a
/// working one agree.
/// </para>
/// </summary>
public sealed class RuleInfluenceTests
{
    private static ScoreLine Line(string code, decimal weight, decimal value) =>
        new(code, weight, value, weight * value, RuleNote.Of($"test.{code}"));

    private static ScoredCandidate Candidate(params ScoreLine[] lines) =>
        new(lines.Sum(line => line.Contribution), lines);

    [Fact]
    public void A_rules_influence_is_its_weight_times_how_much_it_actually_varied()
    {
        // Rotation is weighted four times travel and scores every candidate the
        // same, so it moves nothing. This is the whole thesis of the diagnostic:
        // a weight is a claim about importance that nothing checks.
        var report = RuleInfluence.Of(
        [
            Candidate(Line("rotation", 0.40m, 0.5m), Line("travel", 0.10m, 1.0m)),
            Candidate(Line("rotation", 0.40m, 0.5m), Line("travel", 0.10m, 0.0m))
        ]);

        var rotation = Assert.Single(report.Rules, rule => rule.RuleCode == "rotation");
        var travel = Assert.Single(report.Rules, rule => rule.RuleCode == "travel");

        Assert.Equal(0m, rotation.RealisedSwing);
        Assert.Equal(0.10m, travel.RealisedSwing);

        // Travel is nominally an eighth of the set and is doing all of the ranking.
        Assert.Equal(0.2m, travel.NominalShare);
        Assert.Equal(1m, travel.RealisedShare);

        // Ordered by what did the deciding, not by weight.
        Assert.Equal("travel", report.Rules[0].RuleCode);
    }

    [Fact]
    public void A_low_weighted_rule_that_decides_the_ranking_is_flagged()
    {
        // The shape of the Phase 5 counting bug, in miniature: travel measured
        // against a field of two adjacent bays swings the full width of the score
        // while the rule it should defer to barely moves.
        var report = RuleInfluence.Of(
        [
            Candidate(Line("exposure", 0.20m, 0.30m), Line("travel", 0.15m, 1.0m)),
            Candidate(Line("exposure", 0.20m, 0.37m), Line("travel", 0.15m, 0.0m))
        ]);

        var travel = Assert.Single(report.Rules, rule => rule.RuleCode == "travel");

        Assert.Contains(InfluenceFindings.OutvotesItsWeight, travel.Findings);
        Assert.Contains(InfluenceFindings.UsesTheFullRange, travel.Findings);

        // And the winner won on travel despite being behind on exposure, which is
        // the sentence somebody reviewing the ranking needs to be able to read.
        Assert.Equal(0.15m, travel.MarginContribution);
        Assert.True(Assert.Single(report.Rules, rule => rule.RuleCode == "exposure").MarginContribution < 0m);
    }

    [Fact]
    public void The_heaviest_rule_is_never_accused_of_outvoting_its_weight()
    {
        // A rule set where the most important rule is doing most of the deciding is
        // a rule set working correctly, however lopsided the shares look.
        var report = RuleInfluence.Of(
        [
            Candidate(Line("rotation", 0.45m, 1.0m), Line("travel", 0.10m, 0.5m)),
            Candidate(Line("rotation", 0.45m, 0.0m), Line("travel", 0.10m, 0.5m))
        ]);

        var rotation = Assert.Single(report.Rules, rule => rule.RuleCode == "rotation");

        Assert.Equal(1m, rotation.RealisedShare);
        Assert.DoesNotContain(InfluenceFindings.OutvotesItsWeight, rotation.Findings);
    }

    [Fact]
    public void A_rule_that_separates_nothing_is_reported_as_having_spent_its_weight_on_nothing()
    {
        var report = RuleInfluence.Of(
        [
            Candidate(Line("velocity", 0.25m, 0.5m), Line("travel", 0.15m, 0.8m)),
            Candidate(Line("velocity", 0.25m, 0.5m), Line("travel", 0.15m, 0.2m))
        ]);

        var velocity = Assert.Single(report.Rules, rule => rule.RuleCode == "velocity");

        Assert.Contains(InfluenceFindings.NeverDiscriminates, velocity.Findings);
        Assert.Equal(1, velocity.DistinctValues);
    }

    [Fact]
    public void A_field_of_one_flags_nothing_because_there_was_no_ranking_to_influence()
    {
        // The degenerate case, and it has to be silent rather than loud. A lone
        // candidate makes every rule look like it discriminated nothing, which is
        // true, uninteresting, and would bury the decisions that matter under one
        // finding per rule per single-candidate decision.
        var report = RuleInfluence.Of([Candidate(Line("travel", 0.15m, 1.0m), Line("velocity", 0.25m, 0.5m))]);

        Assert.Equal(1, report.AdmissibleCount);
        Assert.Equal(0m, report.WinningMargin);
        Assert.All(report.Rules, rule => Assert.Empty(rule.Findings));
    }

    [Fact]
    public void An_empty_field_reports_nothing_rather_than_dividing_by_zero()
    {
        var report = RuleInfluence.Of([]);

        Assert.Equal(0, report.AdmissibleCount);
        Assert.Empty(report.Rules);
    }

    [Fact]
    public void The_winning_margin_is_the_gap_the_contributions_have_to_add_up_to()
    {
        var report = RuleInfluence.Of(
        [
            Candidate(Line("a", 0.50m, 0.8m), Line("b", 0.50m, 0.6m)),
            Candidate(Line("a", 0.50m, 0.4m), Line("b", 0.50m, 0.5m))
        ]);

        // 0.70 against 0.45. The margin and the sum of the per-rule contributions
        // are two ways of computing the same number, and a report where they
        // disagreed would be one nobody could reason from.
        Assert.Equal(0.25m, report.WinningMargin);
        Assert.Equal(report.WinningMargin, report.Rules.Sum(rule => rule.MarginContribution));
    }

    [Fact]
    public void A_decision_read_back_from_storage_reports_what_it_did_when_it_was_made()
    {
        // Trace candidates come back keyed by rule code and in whatever order the
        // rows were written, which is why the report groups rather than zipping —
        // a set edited between writing and reading must not silently mislabel two
        // rules as each other.
        var report = RuleInfluence.Of(
        [
            new ScoredCandidate(0.55m, [Line("travel", 0.15m, 1.0m), Line("exposure", 0.20m, 0.5m)]),
            new ScoredCandidate(0.20m, [Line("exposure", 0.20m, 1.0m), Line("travel", 0.15m, 0.0m)])
        ]);

        Assert.Equal(2, report.Rules.Count);
        Assert.Equal(0.15m, Assert.Single(report.Rules, r => r.RuleCode == "travel").RealisedSwing);
        Assert.Equal(0.10m, Assert.Single(report.Rules, r => r.RuleCode == "exposure").RealisedSwing);
    }

    [Fact]
    public void Scores_are_read_from_the_rule_set_that_produced_them_not_from_the_decision_object()
    {
        // The two overloads have to agree, because one reads a decision as it is
        // made and the other reads the same decision out of a table months later,
        // and a supervisor comparing the live figure with the stored one is exactly
        // the person who would notice if they did not.
        var candidates = new[] { new Bin("A"), new Bin("B") };

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway,
            new RuleSetStamp(Guid.Empty, "test", 1),
            candidates,
            new object(),
            [],
            [new Preference("travel", 0.15m, bin => bin.Key == "A" ? 1m : 0m)]);

        var live = RuleInfluence.Of(decision);

        var stored = RuleInfluence.Of(
        [
            .. decision.Candidates
                .Where(evaluation => evaluation.IsAdmissible)
                .OrderBy(evaluation => evaluation.Rank)
                .Select(evaluation => new ScoredCandidate(evaluation.Score, evaluation.Scores))
        ]);

        Assert.Equal(live.WinningMargin, stored.WinningMargin);
        Assert.Equal(live.Rules[0].RealisedSwing, stored.Rules[0].RealisedSwing);
    }

    private sealed record Bin(string Key) : IRuleCandidate
    {
        public Guid Id { get; } = Guid.CreateVersion7();
    }

    private sealed class Preference(string code, decimal weight, Func<Bin, decimal> score)
        : IPreferenceRule<Bin, object>
    {
        public string Code => code;

        public decimal Weight => weight;

        public ScoreContribution Score(Bin candidate, object context) =>
            ScoreContribution.Of(score(candidate), "test.travel");
    }
}
