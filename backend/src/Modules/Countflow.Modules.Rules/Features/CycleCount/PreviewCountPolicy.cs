using System.Security.Claims;
using Countflow.Modules.Rules.CycleCount;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.CycleCount;

public sealed record PreviewCountPolicyRequest(string WarehouseCode, int MaxTasks = 20);

public sealed record PreviewCountLine(
    string LocationCode,
    string? LotNumber,
    decimal ExpectedQuantity,
    int Rank,
    decimal Score);

public sealed record PreviewCountPolicyResponse(
    Guid DecisionTraceId,
    IReadOnlyList<PreviewCountLine> Lines,
    int ConsideredCount,
    int AdmissibleCount,
    DecisionTraceView Trace);

/// <summary>
/// "What would tonight's counts be?" — the same evaluation a count plan runs, with
/// nothing raised.
/// <para>
/// The third preview, and the one whose value is most obvious before anything is
/// committed. A supervisor tightening the A-class interval from thirty days to
/// fourteen can see exactly which bays that pulls forward and which it pushes back,
/// and can see the reasoning behind each, before a single operator is sent
/// anywhere. Nothing is raised, so running it does not change the answer it gave —
/// which matters more here than for the other two, since the scheduling constraint
/// means a raised count is precisely a holding that stops being a candidate.
/// </para>
/// </summary>
public static class PreviewCountPolicy
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/rules/cycle-count/preview", async (
                PreviewCountPolicyRequest request,
                ClaimsPrincipal user,
                CountPolicyAdvisor advisor,
                IDecisionTraceReader traces,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, advisor, traces, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("PreviewCountPolicy")
            .WithSummary("Run the cycle-count policy without raising any work.")
            .Produces<PreviewCountPolicyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<PreviewCountPolicyResponse>> HandleAsync(
        PreviewCountPolicyRequest request,
        ClaimsPrincipal user,
        CountPolicyAdvisor advisor,
        IDecisionTraceReader traces,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<PreviewCountPolicyResponse>(actor.Error);

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<PreviewCountPolicyResponse>(RulesErrors.WarehouseNotFound);

        // No scheduled holdings are passed. A preview asks what the policy says
        // about the warehouse, not what is left after the work already out there —
        // and the module that owns that fact is not this one.
        var advice = await advisor.PreviewAsync(
            new CountPolicyQuery
            {
                WarehouseId = warehouse.Id,
                MaxTasks = request.MaxTasks,
                Actor = actor.Value
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<PreviewCountPolicyResponse>(advice.Error);

        var trace = await traces.FindAsync(advice.Value.DecisionTraceId, cancellationToken);
        if (trace is null) return Result.Failure<PreviewCountPolicyResponse>(RulesErrors.TraceNotFound);

        return new PreviewCountPolicyResponse(
            advice.Value.DecisionTraceId,
            [
                .. advice.Value.Lines.Select(line => new PreviewCountLine(
                    line.LocationCode, line.LotNumber, line.ExpectedQuantity, line.Rank, line.Score))
            ],
            advice.Value.ConsideredCount,
            advice.Value.AdmissibleCount,
            trace);
    }
}
