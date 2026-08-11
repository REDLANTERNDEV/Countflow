using System.Diagnostics;

namespace Countflow.SharedKernel.Rules;

/// <summary>
/// How much of a losing field to keep. See <see cref="Decision{TCandidate}.Candidates"/>.
/// </summary>
/// <param name="RetainedExclusions">
/// Excluded candidates kept in the trace. Enough to show an operator that the
/// obvious bin was considered and why it lost; not so many that a rack aisle's
/// worth of identical rejections is written on every pallet.
/// </param>
public sealed record TraceRetention(int RetainedExclusions)
{
    public static readonly TraceRetention Default = new(30);
}

/// <summary>
/// The one evaluator every rule-driven decision in Countflow goes through.
/// <para>
/// Ordered hard constraints, then weighted soft preferences — the shape ADR 0004
/// fixes, and the reason there is a shared engine at all rather than a
/// hand-written comparison at each decision site. A scattered <c>if</c> that
/// picks a bin cannot be traced, cannot be versioned, and cannot be replayed
/// against history, so the engine is not a convenience: it is the only thing that
/// makes the other two pillars possible.
/// </para>
/// <para>
/// Pure, synchronous and stateless. Everything it needs has already been fetched
/// by the caller, which is what lets Phase 6 replay a candidate rule set against
/// historical inputs at whatever speed it likes, with no database in the loop.
/// </para>
/// </summary>
public static class RuleEngine
{
    public static Decision<TCandidate> Evaluate<TCandidate, TContext>(
        DecisionKind kind,
        RuleSetStamp ruleSet,
        IReadOnlyList<TCandidate> candidates,
        TContext context,
        IReadOnlyList<IConstraintRule<TCandidate, TContext>> constraints,
        IReadOnlyList<IPreferenceRule<TCandidate, TContext>> preferences,
        TraceRetention? retention = null)
        where TCandidate : IRuleCandidate
    {
        var kept = retention ?? TraceRetention.Default;
        var started = Stopwatch.GetTimestamp();

        var admissible = new List<CandidateEvaluation<TCandidate>>();
        var excluded = new List<CandidateEvaluation<TCandidate>>();

        foreach (var candidate in candidates)
        {
            // First failure wins and evaluation stops. Constraints are ordered by
            // the rule set, so the cheap and the decisive ones come first, and the
            // reason an operator is shown is the one the rule author considered
            // most important rather than whichever happened to be evaluated last.
            RuleNote? rejection = null;
            foreach (var constraint in constraints)
            {
                rejection = constraint.Exclude(candidate, context);
                if (rejection is not null)
                {
                    break;
                }
            }

            if (rejection is not null)
            {
                excluded.Add(new CandidateEvaluation<TCandidate>(
                    candidate, Rank: 0, IsAdmissible: false, Score: 0m, rejection, Scores: []));

                continue;
            }

            var scores = new List<ScoreLine>(preferences.Count);
            var total = 0m;

            foreach (var preference in preferences)
            {
                var contribution = preference.Score(candidate, context);

                // Clamped rather than trusted. A preference that returns 3.0
                // because somebody divided by the wrong denominator would quietly
                // outvote every other rule in the set, and the resulting trace
                // would look entirely reasonable.
                var value = Math.Clamp(contribution.Value, 0m, 1m);
                var weighted = preference.Weight * value;

                scores.Add(new ScoreLine(
                    preference.Code, preference.Weight, value, weighted, contribution.Reason));

                total += weighted;
            }

            admissible.Add(new CandidateEvaluation<TCandidate>(
                candidate, Rank: 0, IsAdmissible: true, total, ExcludedBy: null, scores));
        }

        // Ties broken on the candidate key, so the same warehouse in the same
        // state always produces the same suggestion. Determinism is not tidiness
        // here: an operator who is sent to a different bin each time they rescan
        // the same pallet stops believing the system knows anything.
        var ranked = admissible
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Candidate.Key, StringComparer.Ordinal)
            .Select((c, index) => c with { Rank = index + 1 })
            .ToList();

        return new Decision<TCandidate>(
            kind,
            ruleSet,
            [.. ranked, .. Sample(excluded, kept.RetainedExclusions)],
            consideredCount: candidates.Count,
            admissibleCount: ranked.Count,
            Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Trims the losing field to the retention budget, keeping a spread across
    /// reasons rather than a prefix.
    /// <para>
    /// Taking the first N would be simpler and nearly useless. Candidates arrive
    /// in warehouse order, so the first thirty exclusions are thirty dock doors
    /// failing the same constraint, and the trace would never mention the aisle
    /// that was the wrong temperature — the one thing the operator wanted to know.
    /// Round-robining across the distinct reasons means every <i>way</i> a bin
    /// could fail is represented before any way is represented twice.
    /// </para>
    /// </summary>
    private static List<CandidateEvaluation<TCandidate>> Sample<TCandidate>(
        List<CandidateEvaluation<TCandidate>> excluded,
        int budget)
        where TCandidate : IRuleCandidate
    {
        if (excluded.Count <= budget)
        {
            return excluded;
        }

        var byReason = excluded
            .GroupBy(c => c.ExcludedBy!.Code, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();

        var sample = new List<CandidateEvaluation<TCandidate>>(budget);

        for (var round = 0; sample.Count < budget; round++)
        {
            var takenThisRound = false;

            foreach (var group in byReason)
            {
                if (round >= group.Count || sample.Count >= budget)
                {
                    continue;
                }

                sample.Add(group[round]);
                takenThisRound = true;
            }

            if (!takenThisRound)
            {
                break;
            }
        }

        return sample;
    }
}
