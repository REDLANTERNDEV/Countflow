using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Countflow.Modules.Replay.Domain;
using Countflow.Modules.Replay.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Replay.Features;

/// <summary>
/// Runs an experiment and writes down what it found.
/// <para>
/// The whole module in one class, and it is deliberately thin. Every judgement in a
/// replay — which decisions are replayable, how to rebuild the warehouse they were
/// made in, whether the reconstruction can be trusted, what each rule was worth —
/// belongs to Rules and arrives here as an answer. What this owns is the part
/// nobody else should: that the experiment happened, when, against what, and what
/// it said at the time.
/// </para>
/// <para>
/// It is the fourth orchestrator of the same shape, after Inbound, Outbound and
/// Counting, and the one that most obviously could have been written the other way.
/// Putting the run table in the <c>rules</c> schema would have saved a module and a
/// contract — and would have made a rules engine that keeps a log of its own
/// experiments, which is one responsibility too many for the thing every other
/// module depends on. The two have different lifecycles as well: a trace is
/// evidence about a pallet that has already moved and is kept as long as the ledger
/// is, and a run is a note about an argument somebody had last month.
/// </para>
/// </summary>
public sealed class ReplayRunner(
    ReplayDbContext dbContext,
    IShadowReplay replay,
    TimeProvider clock,
    ILogger<ReplayRunner> logger)
{
    public async Task<Result<ReplayRun>> RunAsync(
        ReplayScope scope,
        Guid candidateRuleSetId,
        string? notes,
        PostingActor actor,
        CancellationToken cancellationToken)
    {
        if (scope.From > scope.To)
        {
            return Result.Failure<ReplayRun>(ReplayErrors.WindowInverted);
        }

        var result = await replay.RunAsync(scope, candidateRuleSetId, cancellationToken);
        if (result.IsFailure) return Result.Failure<ReplayRun>(result.Error);

        var comparison = result.Value;

        var run = new ReplayRun
        {
            WarehouseId = scope.WarehouseId,
            Reference = await NextReferenceAsync(scope.WarehouseId, cancellationToken),
            Kind = scope.Kind,
            CandidateRuleSetId = comparison.Candidate.Id,
            CandidateRuleSetName = comparison.Candidate.Name,
            CandidateRuleSetVersion = comparison.Candidate.Version,
            BaselineRuleSetId = comparison.Baseline.Id,
            BaselineRuleSetName = comparison.Baseline.Name,
            BaselineRuleSetVersion = comparison.Baseline.Version,
            FromDate = scope.From,
            ToDate = scope.To,
            ReplayedCount = comparison.ReplayedCount,
            ExactCount = comparison.ExactCount,
            ApproximateCount = comparison.ApproximateCount,
            UnreplayableCount = comparison.UnreplayableCount,
            ChangedCount = comparison.ChangedCount,
            ReorderedCount = comparison.ReorderedCount,
            GainedCount = comparison.GainedCount,
            LostCount = comparison.LostCount,
            ElapsedMilliseconds = comparison.ElapsedMilliseconds,
            InfluenceJson = Json.Serialise(comparison.CandidateInfluence),
            RequestedByUserId = actor.UserId,
            RequestedByUserName = actor.UserName,
            CreatedAt = clock.GetUtcNow(),
            Notes = notes
        };

        dbContext.ReplayRuns.Add(run);

        foreach (var decision in comparison.Decisions)
        {
            // Added through the DbSet rather than through run.Decisions, because
            // every key here is a client-generated UUIDv7 and EF infers Modified
            // rather than Added for a child reached through a tracked parent whose
            // key is already set. The navigation is fixed up from the foreign key.
            dbContext.ReplayRunDecisions.Add(new ReplayRunDecision
            {
                ReplayRunId = run.Id,
                DecisionTraceId = decision.TraceId,
                DecidedAt = decision.DecidedAt,
                Subject = Truncate(decision.Subject, 200),
                Fidelity = decision.Fidelity,
                FidelityNote = decision.FidelityNote,
                Outcome = decision.Outcome,
                RecordedKey = decision.RecordedKey,
                BaselineKey = decision.BaselineKey,
                CandidateKey = decision.CandidateKey,
                BaselineScore = decision.BaselineScore,
                CandidateScore = decision.CandidateScore,
                CandidateRankUnderBaseline = decision.CandidateRankUnderBaseline,
                BaselineRankUnderCandidate = decision.BaselineRankUnderCandidate,
                ConsideredCount = decision.ConsideredCount,
                BaselineAdmissibleCount = decision.BaselineAdmissibleCount,
                CandidateAdmissibleCount = decision.CandidateAdmissibleCount,
                BaselineInfluenceJson = Json.Serialise(decision.BaselineInfluence),
                CandidateInfluenceJson = Json.Serialise(decision.CandidateInfluence)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Replay {Reference}: {Candidate} v{Version} over {Replayed} {Kind} decisions — {Changed} changed, " +
            "{Lost} lost, {Exact} reproduced",
            run.Reference,
            run.CandidateRuleSetName,
            run.CandidateRuleSetVersion,
            run.ReplayedCount,
            run.Kind,
            run.ChangedCount,
            run.LostCount,
            run.ExactCount);

        return run;
    }

    /// <summary>
    /// Counted rather than sequenced, the same way a count plan's reference is.
    /// Two replays started in the same second collide on the unique index and the
    /// loser retries; a database sequence would avoid that and leave gaps, and a
    /// reference people quote at each other should not skip.
    /// </summary>
    private async Task<string> NextReferenceAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var prefix = $"RPL-{clock.GetUtcNow().Year.ToString(CultureInfo.InvariantCulture)}-";

        var used = await dbContext.ReplayRuns
            .AsNoTracking()
            .Where(run => run.WarehouseId == warehouseId && run.Reference.StartsWith(prefix))
            .CountAsync(cancellationToken);

        return $"{prefix}{(used + 1).ToString("D4", CultureInfo.InvariantCulture)}";
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}

/// <summary>
/// One serialiser for the influence blobs, shared by whatever writes them and
/// whatever reads them back — the same argument <c>TraceJson</c> makes in the rules
/// module, and the same failure avoided.
/// </summary>
internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string Serialise<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T? Deserialise<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
