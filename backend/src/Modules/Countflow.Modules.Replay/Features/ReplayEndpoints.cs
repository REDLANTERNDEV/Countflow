using System.Security.Claims;
using Countflow.Modules.Replay.Domain;
using Countflow.Modules.Replay.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Replay.Features;

/// <param name="MaxDecisions">
/// Bounded, because a replay is something somebody waits for. The traces carry
/// <c>ElapsedMicroseconds</c>, so the cost of the rules themselves is known and
/// small; what a large window really buys is a longer walk down the ledger.
/// </param>
public sealed record CreateReplayRunRequest(
    string WarehouseCode,
    DecisionKind Kind,
    Guid CandidateRuleSetId,
    DateTimeOffset From,
    DateTimeOffset To,
    int MaxDecisions = 200,
    string? Notes = null);

public sealed record ReplayDecisionResponse(
    Guid DecisionTraceId,
    DateTimeOffset DecidedAt,
    string Subject,
    ReplayFidelity Fidelity,
    string? FidelityNote,
    ReplayOutcome Outcome,
    string? RecordedKey,
    string? BaselineKey,
    string? CandidateKey,
    decimal BaselineScore,
    decimal CandidateScore,
    int CandidateRankUnderBaseline,
    int BaselineRankUnderCandidate,
    int ConsideredCount,
    int BaselineAdmissibleCount,
    int CandidateAdmissibleCount,
    IReadOnlyList<InfluenceLine> BaselineInfluence,
    IReadOnlyList<InfluenceLine> CandidateInfluence)
{
    public static ReplayDecisionResponse Of(ReplayRunDecision decision) =>
        new(decision.DecisionTraceId,
            decision.DecidedAt,
            decision.Subject,
            decision.Fidelity,
            decision.FidelityNote,
            decision.Outcome,
            decision.RecordedKey,
            decision.BaselineKey,
            decision.CandidateKey,
            decision.BaselineScore,
            decision.CandidateScore,
            decision.CandidateRankUnderBaseline,
            decision.BaselineRankUnderCandidate,
            decision.ConsideredCount,
            decision.BaselineAdmissibleCount,
            decision.CandidateAdmissibleCount,
            Json.Deserialise<List<InfluenceLine>>(decision.BaselineInfluenceJson) ?? [],
            Json.Deserialise<List<InfluenceLine>>(decision.CandidateInfluenceJson) ?? []);
}

public sealed record ReplayRunResponse(
    Guid Id,
    string Reference,
    Guid WarehouseId,
    DecisionKind Kind,
    Guid CandidateRuleSetId,
    string CandidateRuleSetName,
    int CandidateRuleSetVersion,
    string BaselineRuleSetName,
    int BaselineRuleSetVersion,
    DateTimeOffset From,
    DateTimeOffset To,
    int ReplayedCount,
    int ExactCount,
    int ApproximateCount,
    int UnreplayableCount,
    int ChangedCount,
    int ReorderedCount,
    int GainedCount,
    int LostCount,
    long ElapsedMilliseconds,
    IReadOnlyList<RuleInfluenceSummary> Influence,
    string RequestedByUserName,
    DateTimeOffset CreatedAt,
    string? Notes,
    IReadOnlyList<ReplayDecisionResponse> Decisions)
{
    public static ReplayRunResponse Of(ReplayRun run, bool withDecisions) =>
        new(run.Id,
            run.Reference,
            run.WarehouseId,
            run.Kind,
            run.CandidateRuleSetId,
            run.CandidateRuleSetName,
            run.CandidateRuleSetVersion,
            run.BaselineRuleSetName,
            run.BaselineRuleSetVersion,
            run.FromDate,
            run.ToDate,
            run.ReplayedCount,
            run.ExactCount,
            run.ApproximateCount,
            run.UnreplayableCount,
            run.ChangedCount,
            run.ReorderedCount,
            run.GainedCount,
            run.LostCount,
            run.ElapsedMilliseconds,
            Json.Deserialise<List<RuleInfluenceSummary>>(run.InfluenceJson) ?? [],
            run.RequestedByUserName,
            run.CreatedAt,
            run.Notes,
            withDecisions
                ? [.. run.Decisions.OrderBy(d => d.DecidedAt).Select(ReplayDecisionResponse.Of)]
                : []);
}

public sealed record ReplayRunPage(IReadOnlyList<ReplayRunResponse> Items, int Total, int Page, int PageSize);

/// <summary>
/// Runs a candidate rule set against decisions the warehouse actually made.
/// <para>
/// The endpoint ADR 0004 has been describing since Phase 3, and the one that turns
/// "rules are data" from a filing decision into something useful. Supervisor and
/// above, and notably <b>not</b> admin: asking what a change would do is exactly the
/// thing a supervisor should be able to do freely and often. Promoting the answer
/// onto the floor is the admin-only step.
/// </para>
/// </summary>
public static class CreateReplayRun
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/replay/runs", async (
                CreateReplayRunRequest request,
                ClaimsPrincipal user,
                ReplayRunner runner,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, runner, warehouses, cancellationToken)).ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Replay")
            .WithName("CreateReplayRun")
            .WithSummary("Replay a candidate rule set against real historical decisions.")
            .Produces<ReplayRunResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<ReplayRunResponse>> HandleAsync(
        CreateReplayRunRequest request,
        ClaimsPrincipal user,
        ReplayRunner runner,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<ReplayRunResponse>(actor.Error);

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<ReplayRunResponse>(ReplayErrors.WarehouseNotFound);

        var run = await runner.RunAsync(
            new ReplayScope(warehouse.Id, request.Kind, request.From, request.To, request.MaxDecisions),
            request.CandidateRuleSetId,
            request.Notes,
            actor.Value,
            cancellationToken);

        return run.IsFailure
            ? Result.Failure<ReplayRunResponse>(run.Error)
            : ReplayRunResponse.Of(run.Value, withDecisions: true);
    }
}

/// <summary>The experiments this warehouse has run, newest first.</summary>
public static class GetReplayRuns
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/replay/runs", async (
                Guid? warehouseId,
                DecisionKind? kind,
                int? page,
                int? pageSize,
                ReplayDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var currentPage = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? 25, 1, 100);

                var query = dbContext.ReplayRuns
                    .AsNoTracking()
                    .Where(run => (warehouseId == null || run.WarehouseId == warehouseId)
                                  && (kind == null || run.Kind == kind));

                var total = await query.CountAsync(cancellationToken);

                // Filtered and ordered before anything is projected, in the module
                // directories' idiom and for the same reason.
                var runs = await query
                    .OrderByDescending(run => run.CreatedAt)
                    .Skip((currentPage - 1) * size)
                    .Take(size)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new ReplayRunPage(
                    [.. runs.Select(run => ReplayRunResponse.Of(run, withDecisions: false))],
                    total,
                    currentPage,
                    size));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Replay")
            .WithName("GetReplayRuns")
            .WithSummary("List replay runs, newest first.")
            .Produces<ReplayRunPage>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}

/// <summary>
/// One run and every decision in it.
/// <para>
/// Everything, not only what changed. A run where nothing changed is a real and
/// useful result — it is what "this tuning is safe" looks like — and a screen that
/// showed only differences would make an empty answer indistinguishable from a
/// broken one.
/// </para>
/// </summary>
public static class GetReplayRun
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/replay/runs/{id:guid}", async (
                Guid id,
                ReplayOutcome? outcome,
                ReplayDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var run = await dbContext.ReplayRuns
                    .AsNoTracking()
                    .Include(r => r.Decisions.Where(d => outcome == null || d.Outcome == outcome))
                    .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

                return run is null
                    ? Result.Failure<ReplayRunResponse>(ReplayErrors.RunNotFound).ToHttpResult()
                    : Results.Ok(ReplayRunResponse.Of(run, withDecisions: true));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Replay")
            .WithName("GetReplayRun")
            .WithSummary("One replay run and the decisions it compared.")
            .Produces<ReplayRunResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
