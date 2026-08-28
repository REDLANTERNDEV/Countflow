using System.Security.Claims;
using Countflow.Modules.Rules.Dispositions;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.Dispositions;

/// <param name="AtLocationCode">
/// The returns bay the goods would be standing in. Supplied rather than guessed,
/// because it is what a quarantine leaves them in and a preview that invented one
/// would be answering a different question from the one the returns desk asks.
/// </param>
/// <param name="DaysSinceDespatch">
/// How long the customer had it. Omitted, the preview is of a blind return, which
/// scores the freshness rule at its midpoint rather than at either end.
/// </param>
public sealed record PreviewDispositionRequest(
    string Sku,
    decimal Quantity,
    string AtLocationCode,
    StockCondition Condition,
    bool IsSealed = false,
    bool IsRepairable = false,
    int? DaysSinceDespatch = null,
    DateOnly? ExpiryDate = null);

public sealed record PreviewDispositionResponse(
    Guid DecisionTraceId,
    Disposition? Disposition,
    decimal Score,
    int ConsideredCount,
    int AdmissibleCount,
    DecisionTraceView Trace);

/// <summary>
/// "What would you do with this?" — the same evaluation the returns desk runs,
/// with nothing posted.
/// <para>
/// The fourth of these, and the one most likely to be used in anger. A returns
/// policy is the rule set a warehouse argues about most, because every weight on
/// it is somebody's money: the difference between restocking and scrapping a
/// pallet is a number on a report either way, and the only way to settle it before
/// it is a pallet is to ask the rules a hypothetical and read the trace.
/// </para>
/// <para>
/// It shares the advisor rather than reimplementing it, so a preview cannot
/// disagree with the real thing — the same guarantee, for the same reason, as the
/// other three.
/// </para>
/// </summary>
public static class PreviewDisposition
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rules/disposition/preview", async (
                PreviewDispositionRequest request,
                ClaimsPrincipal user,
                DispositionAdvisor advisor,
                IDecisionTraceReader traces,
                IItemDirectory items,
                ILocationDirectory locations,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, advisor, traces, items, locations, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("PreviewDisposition")
            .WithSummary("Run the returns disposition rules without posting anything.")
            .Produces<PreviewDispositionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<PreviewDispositionResponse>> HandleAsync(
        PreviewDispositionRequest request,
        ClaimsPrincipal user,
        DispositionAdvisor advisor,
        IDecisionTraceReader traces,
        IItemDirectory items,
        ILocationDirectory locations,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<PreviewDispositionResponse>(actor.Error);

        var item = await items.FindBySkuAsync(request.Sku.Trim(), cancellationToken);
        if (item is null) return Result.Failure<PreviewDispositionResponse>(RulesErrors.ItemNotFound);

        var bay = await locations.FindByCodeAsync(request.AtLocationCode.Trim(), cancellationToken);
        if (bay is null) return Result.Failure<PreviewDispositionResponse>(RulesErrors.LocationNotFound);

        var advice = await advisor.PreviewAsync(
            new DispositionQuery
            {
                WarehouseId = bay.WarehouseId,
                ItemId = item.Id,
                LotId = null,
                Quantity = request.Quantity,
                AtLocationId = bay.Id,
                Findings = new InspectionFindings
                {
                    Condition = request.Condition,
                    IsSealed = request.IsSealed,
                    IsRepairable = request.IsRepairable
                },
                DaysSinceDespatch = request.DaysSinceDespatch,
                ExpiryDate = request.ExpiryDate,
                Actor = actor.Value
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<PreviewDispositionResponse>(advice.Error);

        var trace = await traces.FindAsync(advice.Value.DecisionTraceId, cancellationToken);
        if (trace is null) return Result.Failure<PreviewDispositionResponse>(RulesErrors.TraceNotFound);

        return new PreviewDispositionResponse(
            advice.Value.DecisionTraceId,
            advice.Value.Disposition,
            advice.Value.Score,
            advice.Value.ConsideredCount,
            advice.Value.AdmissibleCount,
            trace);
    }
}
