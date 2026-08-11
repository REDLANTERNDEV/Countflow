using System.Security.Claims;
using Countflow.Modules.Rules.Allocation;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.Allocation;

/// <param name="WarehouseCode">
/// Which building to look in. Allocation starts from stock rather than from a
/// location, so unlike the putaway preview there is no origin to infer the
/// warehouse from.
/// </param>
public sealed record PreviewAllocationRequest(
    string WarehouseCode,
    string Sku,
    decimal Quantity,
    DateOnly? ShipBy = null);

public sealed record PreviewAllocationLine(
    string LocationCode,
    string? LotNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal Score);

public sealed record PreviewAllocationResponse(
    Guid DecisionTraceId,
    IReadOnlyList<PreviewAllocationLine> Lines,
    decimal AllocatedQuantity,
    decimal ShortfallQuantity,
    int ConsideredCount,
    int AdmissibleCount,
    DecisionTraceView Trace);

/// <summary>
/// "What would this order take?" — the same evaluation a release runs, with
/// nothing reserved.
/// <para>
/// The allocation twin of the putaway preview, and the more useful of the two in
/// practice: a supervisor about to change the shelf-life policy or the rotation
/// weight can see which batch tomorrow's orders would draw on before they commit
/// to it. Nothing is held, so running it does not make the answer it gave wrong.
/// </para>
/// </summary>
public static class PreviewAllocation
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/rules/allocation/preview", async (
                PreviewAllocationRequest request,
                ClaimsPrincipal user,
                AllocationAdvisor advisor,
                IDecisionTraceReader traces,
                IItemDirectory items,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
                    (await HandleAsync(request, user, advisor, traces, items, warehouses, cancellationToken))
                        .ToHttpResult())
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("PreviewAllocation")
            .WithSummary("Run the allocation rules without reserving anything.")
            .Produces<PreviewAllocationResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Result<PreviewAllocationResponse>> HandleAsync(
        PreviewAllocationRequest request,
        ClaimsPrincipal user,
        AllocationAdvisor advisor,
        IDecisionTraceReader traces,
        IItemDirectory items,
        IWarehouseDirectory warehouses,
        CancellationToken cancellationToken)
    {
        var actor = PostingActor.From(user);
        if (actor.IsFailure) return Result.Failure<PreviewAllocationResponse>(actor.Error);

        var item = await items.FindBySkuAsync(request.Sku.Trim(), cancellationToken);
        if (item is null) return Result.Failure<PreviewAllocationResponse>(RulesErrors.ItemNotFound);

        var warehouse = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
        if (warehouse is null) return Result.Failure<PreviewAllocationResponse>(RulesErrors.WarehouseNotFound);

        var plan = await advisor.PreviewAsync(
            new AllocationQuery
            {
                WarehouseId = warehouse.Id,
                ItemId = item.Id,
                Quantity = request.Quantity,
                ShipBy = request.ShipBy,
                Actor = actor.Value
            },
            cancellationToken);

        if (plan.IsFailure) return Result.Failure<PreviewAllocationResponse>(plan.Error);

        var trace = await traces.FindAsync(plan.Value.DecisionTraceId, cancellationToken);
        if (trace is null) return Result.Failure<PreviewAllocationResponse>(RulesErrors.TraceNotFound);

        return new PreviewAllocationResponse(
            plan.Value.DecisionTraceId,
            [
                .. plan.Value.Lines.Select(line => new PreviewAllocationLine(
                    line.LocationCode, line.LotNumber, line.ExpiryDate, line.Quantity, line.Score))
            ],
            plan.Value.AllocatedQuantity,
            plan.Value.ShortfallQuantity,
            plan.Value.ConsideredCount,
            plan.Value.AdmissibleCount,
            trace);
    }
}
