using Countflow.Modules.Rules.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Rules.Integration;

/// <summary>
/// The Rules module's published read surface for stored decisions (ADR 0002).
/// <para>
/// Work and the handheld both need to answer "why here?" and neither makes a
/// decision, so the reading side is published separately from
/// <see cref="IPutawayAdvisor"/>.
/// </para>
/// </summary>
public sealed class DecisionTraceReader(RulesDbContext dbContext) : IDecisionTraceReader
{
    public async Task<DecisionTraceView?> FindAsync(Guid traceId, CancellationToken cancellationToken)
    {
        var trace = await dbContext.DecisionTraces
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == traceId, cancellationToken);

        if (trace is null)
        {
            return null;
        }

        var candidates = await dbContext.DecisionTraceCandidates
            .AsNoTracking()
            .Where(c => c.DecisionTraceId == traceId)
            // Admissible first in rank order, then the excluded — matching the
            // order the engine produced, because a trace read back in a different
            // order than it was decided invites the reader to infer a ranking that
            // was never there. Excluded candidates carry rank 0, so descending on
            // admissibility then ascending on rank reproduces it exactly.
            .OrderByDescending(c => c.IsAdmissible)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.CandidateKey)
            .ToListAsync(cancellationToken);

        return new DecisionTraceView(
            trace.Id,
            trace.Kind,
            trace.RuleSetName,
            trace.RuleSetVersion,
            trace.DecidedAt,
            trace.ConsideredCount,
            trace.AdmissibleCount,
            trace.SelectedCandidateId,
            trace.SelectedCandidateKey,
            [
                .. candidates.Select(c => new TracedCandidate(
                    c.CandidateId,
                    c.CandidateKey,
                    c.Rank,
                    c.IsAdmissible,
                    c.Score,
                    TraceJson.Deserialise<RuleNote>(c.ExcludedByJson),
                    TraceJson.Deserialise<List<ScoreLine>>(c.ScoresJson) ?? []))
            ]);
    }
}
