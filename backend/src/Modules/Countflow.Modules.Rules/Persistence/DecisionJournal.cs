using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.Persistence;

/// <summary>
/// Writes a decision down.
/// <para>
/// Takes a <see cref="Decision{TCandidate}"/> and nothing else, which is the
/// point: the only way to obtain one is to run the engine, so there is no shape
/// of call that records an outcome without the reasoning behind it. ADR 0004 asks
/// for explainability that cannot rot, and a journal that accepted a bare winner
/// would be the first place it rotted.
/// </para>
/// </summary>
public sealed class DecisionJournal(RulesDbContext dbContext, TimeProvider clock)
{
    public async Task<Guid> RecordAsync<TCandidate>(
        Decision<TCandidate> decision,
        object inputs,
        PostingActor actor,
        bool isPreview,
        CancellationToken cancellationToken)
        where TCandidate : IRuleCandidate
    {
        var winner = decision.Winner;

        var trace = new DecisionTrace
        {
            Kind = decision.Kind,
            RuleSetId = decision.RuleSet.Id,
            RuleSetName = decision.RuleSet.Name,
            RuleSetVersion = decision.RuleSet.Version,
            DecidedAt = clock.GetUtcNow(),
            ConsideredCount = decision.ConsideredCount,
            AdmissibleCount = decision.AdmissibleCount,
            ElapsedMicroseconds = (long)(decision.Elapsed.TotalMicroseconds),
            SelectedCandidateId = winner?.Candidate.Id,
            SelectedCandidateKey = winner?.Candidate.Key,
            InputsJson = TraceJson.Serialise(inputs),
            IsPreview = isPreview,
            UserId = actor.UserId,
            UserName = actor.UserName
        };

        dbContext.DecisionTraces.Add(trace);

        foreach (var candidate in decision.Candidates)
        {
            dbContext.DecisionTraceCandidates.Add(new DecisionTraceCandidate
            {
                DecisionTraceId = trace.Id,
                CandidateId = candidate.Candidate.Id,
                CandidateKey = candidate.Candidate.Key,
                Rank = candidate.Rank,
                IsAdmissible = candidate.IsAdmissible,
                Score = candidate.Score,
                ExcludedByJson = candidate.ExcludedBy is null ? null : TraceJson.Serialise(candidate.ExcludedBy),
                ScoresJson = TraceJson.Serialise(candidate.Scores)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return trace.Id;
    }
}
