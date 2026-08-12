using System.Security.Claims;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.Putaway;

/// <param name="FromLocationCode">
/// Where the goods would be standing. Defaults to the warehouse's first receiving
/// dock is deliberately <i>not</i> done — the origin is excluded as a candidate,
/// so guessing it would quietly change the answer.
/// </param>
public sealed record PreviewPutawayRequest(
    string Sku,
    decimal Quantity,
    string FromLocationCode,
    string? LotNumber = null,
    StockCondition? Condition = null);

public sealed record PreviewPutawayResponse(
    Guid DecisionTraceId,
    string? LocationCode,
    decimal Score,
    int ConsideredCount,
    int AdmissibleCount,
    DecisionTraceView Trace);

/// <summary>
/// "Where would you put this?" — the same evaluation the dock runs, with nothing
/// posted.
/// <para>
/// The cheap half of ADR 0004's second promise. Full shadow replay over history
/// is Phase 6; this is the interactive version, and it exists now because a
/// supervisor who has just edited a weight wants an answer in a second, not a
/// report tomorrow. It shares the advisor rather than reimplementing it, so a
/// preview cannot disagree with the real thing.
/// </para>
/// </summary>
public static class PreviewPutaway
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rules/putaway/preview", async (
                PreviewPutawayRequest request,
                ClaimsPrincipal user,
                PutawayAdvisor advisor,
                IDecisionTraceReader traces,
                IItemDirectory items,
                ILocationDirectory locations,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, advisor, traces, items, locations, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("PreviewPutaway")
            .WithSummary("Run the putaway rules without posting anything.")
            .Produces<PreviewPutawayResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<PreviewPutawayResponse>> HandleAsync(
        PreviewPutawayRequest request,
        ClaimsPrincipal user,
        PutawayAdvisor advisor,
        IDecisionTraceReader traces,
        IItemDirectory items,
        ILocationDirectory locations,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<PreviewPutawayResponse>(actor.Error);

        var item = await items.FindBySkuAsync(request.Sku.Trim(), cancellationToken);
        if (item is null) return Result.Failure<PreviewPutawayResponse>(RulesErrors.ItemNotFound);

        var origin = await locations.FindByCodeAsync(request.FromLocationCode.Trim(), cancellationToken);
        if (origin is null) return Result.Failure<PreviewPutawayResponse>(RulesErrors.LocationNotFound);

        // A preview cannot resolve a lot without reaching into Inventory's lot
        // table, which it must not. Lot-sensitive rules therefore behave as they
        // would for a brand-new batch, and that is the honest answer to "where
        // would a fresh pallet go" — which is the question this endpoint is for.
        var advice = await advisor.PreviewAsync(
            new PutawayQuery
            {
                WarehouseId = origin.WarehouseId,
                ItemId = item.Id,
                LotId = null,
                Quantity = request.Quantity,
                FromLocationId = origin.Id,
                Condition = request.Condition ?? StockCondition.Available,
                Actor = actor.Value
            },
            cancellationToken);

        if (advice.IsFailure) return Result.Failure<PreviewPutawayResponse>(advice.Error);

        var trace = await traces.FindAsync(advice.Value.DecisionTraceId, cancellationToken);
        if (trace is null) return Result.Failure<PreviewPutawayResponse>(RulesErrors.TraceNotFound);

        return new PreviewPutawayResponse(
            advice.Value.DecisionTraceId,
            advice.Value.LocationCode,
            advice.Value.Score,
            advice.Value.ConsideredCount,
            advice.Value.AdmissibleCount,
            trace);
    }
}
