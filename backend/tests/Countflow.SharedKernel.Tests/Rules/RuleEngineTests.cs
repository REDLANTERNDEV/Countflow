using Countflow.SharedKernel.Rules;

namespace Countflow.SharedKernel.Tests.Rules;

/// <summary>
/// The properties ADR 0004 claims about the shared evaluator.
/// <para>
/// Pure and synchronous, so these need no database and no host — which is itself
/// the point being tested. An engine that could only be exercised against a live
/// warehouse could not be replayed over history in Phase 6 either.
/// </para>
/// </summary>
public sealed class RuleEngineTests
{
    private sealed record Bin(Guid Id, string Key, int Distance, bool Blocked, bool Chilled) : IRuleCandidate;

    private sealed record Need(bool WantsChilled);

    private static readonly RuleSetStamp Stamp = new(Guid.CreateVersion7(), "test", 3);

    private static Bin NewBin(string key, int distance = 0, bool blocked = false, bool chilled = false) =>
        new(Guid.CreateVersion7(), key, distance, blocked, chilled);

    private sealed class NotBlocked : IConstraintRule<Bin, Need>
    {
        public string Code => "test.not_blocked";

        public RuleNote? Exclude(Bin candidate, Need context) =>
            candidate.Blocked ? RuleNote.Of("test.blocked", ("bin", candidate.Key)) : null;
    }

    private sealed class TemperatureMatches : IConstraintRule<Bin, Need>
    {
        public string Code => "test.temperature";

        public RuleNote? Exclude(Bin candidate, Need context) =>
            candidate.Chilled == context.WantsChilled
                ? null
                : RuleNote.Of("test.temperature_mismatch", ("wanted", context.WantsChilled ? "Chilled" : "Ambient"));
    }

    private sealed class Closer(decimal weight) : IPreferenceRule<Bin, Need>
    {
        public string Code => "test.closer";

        public decimal Weight => weight;

        public ScoreContribution Score(Bin candidate, Need context) =>
            ScoreContribution.Of(
                1m - (candidate.Distance / 100m),
                "test.distance",
                ("metres", candidate.Distance.ToString()));
    }

    /// <summary>Deliberately misbehaved: returns values outside [0, 1].</summary>
    private sealed class Unbounded : IPreferenceRule<Bin, Need>
    {
        public string Code => "test.unbounded";

        public decimal Weight => 1m;

        public ScoreContribution Score(Bin candidate, Need context) =>
            ScoreContribution.Of(candidate.Distance == 0 ? 12m : -5m, "test.unbounded");
    }

    [Fact]
    public void The_winner_cannot_be_read_without_the_reasoning_that_produced_it()
    {
        var near = NewBin("A-01", distance: 10);
        var far = NewBin("A-99", distance: 90);

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [far, near], new Need(false), [], [new Closer(1m)]);

        // There is no shape of this API that yields a bare answer: the winner is a
        // candidate evaluation, and a candidate evaluation carries its scores.
        var winner = decision.Winner;

        Assert.NotNull(winner);
        Assert.Equal("A-01", winner.Candidate.Key);
        Assert.Single(winner.Scores);
        Assert.Equal("test.closer", winner.Scores[0].RuleCode);
        Assert.Equal("test.distance", winner.Scores[0].Reason.Code);
        Assert.Equal("10", winner.Scores[0].Reason.Args["metres"]);

        // And the loser is in the trace too, scored, not merely absent.
        var loser = decision.Candidates.Single(c => c.Candidate.Key == "A-99");
        Assert.True(loser.IsAdmissible);
        Assert.Equal(2, loser.Rank);
    }

    [Fact]
    public void An_excluded_candidate_names_the_constraint_that_excluded_it()
    {
        var blocked = NewBin("A-01", blocked: true);
        var open = NewBin("A-02");

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [blocked, open], new Need(false),
            [new NotBlocked(), new TemperatureMatches()], []);

        var excluded = decision.Candidates.Single(c => c.Candidate.Key == "A-01");

        Assert.False(excluded.IsAdmissible);
        Assert.Equal(0, excluded.Rank);
        Assert.Equal("test.blocked", excluded.ExcludedBy!.Code);
        Assert.Equal("A-01", excluded.ExcludedBy.Args["bin"]);

        Assert.Equal(2, decision.ConsideredCount);
        Assert.Equal(1, decision.AdmissibleCount);
    }

    [Fact]
    public void Only_the_first_failing_constraint_is_reported()
    {
        // Blocked *and* the wrong temperature. An operator needs one reason, not a
        // list of every consequence of the same unsuitability.
        var hopeless = NewBin("A-01", blocked: true, chilled: true);

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [hopeless], new Need(false),
            [new NotBlocked(), new TemperatureMatches()], []);

        Assert.Null(decision.Winner);
        Assert.Equal("test.blocked", decision.Candidates[0].ExcludedBy!.Code);
    }

    [Fact]
    public void Constraint_order_decides_which_reason_the_operator_sees()
    {
        var hopeless = NewBin("A-01", blocked: true, chilled: true);

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [hopeless], new Need(false),
            [new TemperatureMatches(), new NotBlocked()], []);

        // Same candidate, same rules, different order — and the explanation
        // changes. That is why rule order is data a supervisor can edit.
        Assert.Equal("test.temperature_mismatch", decision.Candidates[0].ExcludedBy!.Code);
    }

    [Fact]
    public void Nothing_admissible_is_an_answer_rather_than_a_failure()
    {
        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp,
            [NewBin("A-01", blocked: true), NewBin("A-02", blocked: true)],
            new Need(false), [new NotBlocked()], []);

        Assert.Null(decision.Winner);
        Assert.Equal(0, decision.AdmissibleCount);

        // The useful part: it can still say why, for every bin it looked at.
        Assert.Equal(2, decision.Candidates.Count);
        Assert.All(decision.Candidates, c => Assert.NotNull(c.ExcludedBy));
    }

    [Fact]
    public void Weights_are_what_rank_admissible_candidates()
    {
        var near = NewBin("A-01", distance: 10);
        var far = NewBin("A-99", distance: 90);

        var byDistance = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [near, far], new Need(false), [], [new Closer(1m)]);

        Assert.Equal("A-01", byDistance.Winner!.Candidate.Key);

        // Turn the weight down to nothing and the ranking collapses to the tie
        // break, which is the candidate key. Retuning is a data change.
        var indifferent = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [far, near], new Need(false), [], [new Closer(0m)]);

        Assert.Equal("A-01", indifferent.Winner!.Candidate.Key);
        Assert.Equal(0m, indifferent.Winner.Score);
    }

    [Fact]
    public void A_preference_that_scores_out_of_range_cannot_outvote_the_rest()
    {
        var near = NewBin("A-01", distance: 0);
        var far = NewBin("A-99", distance: 100);

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [near, far], new Need(false), [],
            [new Unbounded(), new Closer(1m)]);

        // 12 and -5 are clamped to 1 and 0, so the misbehaving rule contributes at
        // most its own weight and the trace shows the clamped value rather than
        // the one it asked for.
        var winner = decision.Winner!;
        Assert.Equal("A-01", winner.Candidate.Key);
        Assert.Equal(1m, winner.Scores.Single(s => s.RuleCode == "test.unbounded").Value);
        Assert.Equal(2m, winner.Score);

        var loser = decision.Candidates.Single(c => c.Candidate.Key == "A-99");
        Assert.Equal(0m, loser.Scores.Single(s => s.RuleCode == "test.unbounded").Value);
    }

    [Fact]
    public void The_same_field_in_the_same_state_always_produces_the_same_winner()
    {
        // Two bins that every rule scores identically. Without a deterministic tie
        // break an operator rescanning the same pallet would be sent somewhere
        // else, and would stop believing the system knows anything.
        var first = NewBin("A-01", distance: 50);
        var second = NewBin("A-02", distance: 50);

        var forwards = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [first, second], new Need(false), [], [new Closer(1m)]);

        var backwards = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, [second, first], new Need(false), [], [new Closer(1m)]);

        Assert.Equal("A-01", forwards.Winner!.Candidate.Key);
        Assert.Equal("A-01", backwards.Winner!.Candidate.Key);
    }

    [Fact]
    public void The_trace_bounds_what_it_keeps_but_never_what_it_claims_to_have_seen()
    {
        var candidates = Enumerable.Range(0, 400)
            .Select(index => NewBin($"A-{index:D3}", blocked: true))
            .ToList();

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, candidates, new Need(false),
            [new NotBlocked()], [], new TraceRetention(RetainedExclusions: 5));

        // Four hundred bins failed the same constraint. Storing that sentence four
        // hundred times per pallet is what ADR 0004 warns about; understating how
        // much of the warehouse was examined would be worse.
        Assert.Equal(5, decision.Candidates.Count);
        Assert.Equal(400, decision.ConsideredCount);
        Assert.Equal(0, decision.AdmissibleCount);
    }

    [Fact]
    public void Every_admissible_candidate_is_kept_however_many_there_are()
    {
        var candidates = Enumerable.Range(0, 60)
            .Select(index => NewBin($"A-{index:D3}", distance: index))
            .ToList();

        var decision = RuleEngine.Evaluate(
            DecisionKind.Putaway, Stamp, candidates, new Need(false), [], [new Closer(1m)],
            new TraceRetention(RetainedExclusions: 5));

        // Retention trims the losers, never the shortlist: "which bins would have
        // done, and how close was it" is the question a supervisor actually asks.
        Assert.Equal(60, decision.Candidates.Count);
        Assert.Equal(60, decision.AdmissibleCount);
        Assert.Equal([1, 2, 3], decision.Candidates.Take(3).Select(c => c.Rank));
    }
}
