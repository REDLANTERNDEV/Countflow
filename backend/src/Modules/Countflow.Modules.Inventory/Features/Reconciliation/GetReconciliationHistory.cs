using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Inventory.Features.Reconciliation;

public sealed record ReconciliationRunResponse(
    Guid Id,
    DateTimeOffset RunAt,
    ReconciliationTrigger Trigger,
    long MovementsReplayed,
    int HoldingsChecked,
    decimal LedgerSum,
    int DriftCount,
    decimal WorstDrift,
    long ElapsedMilliseconds,
    bool IsClean);

/// <summary>
/// What the reconciler has found, pass by pass.
/// <para>
/// The accuracy screen's third column, and the reason it is a read rather than
/// another computation: the reconciler already replays the whole ledger against the
/// projection and reports drift, and a dashboard that worked the same thing out its
/// own way would be a second answer to "is the stock right". Two answers to that
/// question is the precise problem the reconciler exists to prevent, so this
/// endpoint reads what it produced and adds nothing.
/// </para>
/// <para>
/// A history of clean runs looks like a table of zeroes and is exactly what it
/// should look like. The claim being evidenced is not "we found and fixed drift",
/// it is "we check hourly, here is every check, and none of them has ever found
/// anything" — which is only worth something if the failures would have been
/// recorded too.
/// </para>
/// </summary>
public static class GetReconciliationHistory
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/reconciliation/history", async (
                int? limit,
                ReconciliationJournal journal,
                CancellationToken cancellationToken) =>
            {
                var runs = await journal.RecentAsync(limit ?? 50, cancellationToken);

                return Results.Ok(runs
                    .Select(run => new ReconciliationRunResponse(
                        run.Id,
                        run.RunAt,
                        run.Trigger,
                        run.MovementsReplayed,
                        run.HoldingsChecked,
                        run.LedgerSum,
                        run.DriftCount,
                        run.WorstDrift,
                        run.ElapsedMilliseconds,
                        run.IsClean))
                    .ToArray());
            })
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("GetReconciliationHistory")
            .WithSummary("Recent reconciliation passes, newest first.")
            .Produces<IReadOnlyList<ReconciliationRunResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
