using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Features.Reconciliation;

public sealed record BalanceDriftResponse(
    string Sku,
    string? LotNumber,
    string LocationCode,
    InventoryStatus Status,
    decimal Projected,
    decimal Replayed,
    decimal Difference,
    long LastMovementSequence);

/// <param name="LedgerSum">
/// The sum of every signed posting. Zero, always: stock is only moved, never
/// created or destroyed. A non-zero figure here means an entry was written with
/// sides that do not balance, which is a deeper problem than drift.
/// </param>
public sealed record ReconciliationResponse(
    DateTimeOffset RunAt,
    long MovementsReplayed,
    int HoldingsChecked,
    decimal LedgerSum,
    bool IsClean,
    IReadOnlyList<BalanceDriftResponse> Drift);

/// <summary>
/// Replays the whole ledger on demand and reports any disagreement with the stored
/// balances.
/// <para>
/// The same pass runs on a timer (<see cref="PeriodicReconciliation"/>); this
/// endpoint exists so a supervisor can demand the proof rather than wait for it,
/// and so the guarantee is demonstrable in a review meeting. Read-only: it reports
/// drift and never repairs it, because the repair is a compensating entry posted by
/// someone who understands what happened, and overwriting the evidence would
/// destroy the only record of which code path caused it.
/// </para>
/// </summary>
public static class RunReconciliation
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/reconciliation", async (
                LedgerReconciler reconciler,
                IItemDirectory items,
                ILocationDirectory locations,
                InventoryDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var report = await reconciler.RunAsync(cancellationToken);

                var drift = await DescribeAsync(dbContext, items, locations, report.Drift, cancellationToken);

                return Results.Ok(new ReconciliationResponse(
                    report.RunAt,
                    report.MovementsReplayed,
                    report.HoldingsChecked,
                    report.LedgerSum,
                    report.IsClean,
                    drift));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Inventory")
            .WithName("RunReconciliation")
            .WithSummary("Replay the ledger and report any drift in the balance projection.")
            .Produces<ReconciliationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<List<BalanceDriftResponse>> DescribeAsync(
        InventoryDbContext dbContext,
        IItemDirectory items,
        ILocationDirectory locations,
        IReadOnlyList<BalanceDrift> drift,
        CancellationToken cancellationToken)
    {
        if (drift.Count == 0)
        {
            // The expected case, and the reason this is worth checking on a timer:
            // an alarm that never fires is doing its job.
            return [];
        }

        var itemsById = await items.FindManyAsync(
            [.. drift.Select(d => d.ItemId).Distinct()], cancellationToken);

        var locationsById = await locations.FindManyAsync(
            [.. drift.Select(d => d.LocationId).Distinct()], cancellationToken);

        var lotIds = drift.Where(d => d.LotId is not null).Select(d => d.LotId!.Value).Distinct().ToList();

        var lotsById = lotIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Lots
                .AsNoTracking()
                .Where(l => lotIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.LotNumber, cancellationToken);

        return
        [
            .. drift.Select(d => new BalanceDriftResponse(
                itemsById.GetValueOrDefault(d.ItemId)?.Sku ?? string.Empty,
                d.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId) : null,
                locationsById.GetValueOrDefault(d.LocationId)?.Code ?? string.Empty,
                d.Status,
                d.Projected,
                d.Replayed,
                d.Difference,
                d.LastMovementSequence))
        ];
    }
}
