using Countflow.Modules.Inventory.Counting;
using Countflow.Modules.Inventory.Features.Accuracy;
using Countflow.Modules.Inventory.Features.Adjustments;
using Countflow.Modules.Inventory.Features.Enquiry;
using Countflow.Modules.Inventory.Features.Receipts;
using Countflow.Modules.Inventory.Features.Reconciliation;
using Countflow.Modules.Inventory.Features.Reversals;
using Countflow.Modules.Inventory.Features.Scrap;
using Countflow.Modules.Inventory.Features.StatusChanges;
using Countflow.Modules.Inventory.Features.Transfers;
using Countflow.Modules.Inventory.Integration;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.Inventory.Reservations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Inventory;

/// <summary>
/// Inventory truth: the append-only stock ledger and the balance projection
/// derived from it (ADR 0001).
/// </summary>
public sealed class InventoryModule : ICountflowModule
{
    public string Name => "Inventory";

    public string Schema => InventoryDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<InventoryDbContext>(connectionString, Schema);

        services.AddScoped<StockLedger>();
        services.AddScoped<StockReferenceResolver>();
        services.AddScoped<LedgerReplay>();
        services.AddScoped<LedgerReconciler>();

        // Phase 4's addition, and deliberately a sibling of the ledger rather than
        // part of it: a reserved quantity is a claim on stock, not a movement of
        // it (see StockReservation).
        services.AddScoped<ReservationLedger>();

        // Phase 5's, and a sibling for the same reason: a count is an observation
        // about the record rather than a change to it, and most counts post nothing
        // at all (see StockCount). It sits beside the ledger because the counts that
        // do find something have to write the finding and the correction together.
        services.AddScoped<StockCounting>();

        // Kept apart from LedgerReconciler deliberately. The reconciler stays a pure
        // read of the ledger it audits; the journal is what writes down what it
        // found, so the accuracy screen can read the reconciler's answer rather than
        // computing a second one.
        services.AddScoped<ReconciliationJournal>();

        // The published boundary (ADR 0002). Inbound, Work and Outbound move stock
        // through these and cannot name StockLedger, so the single posting path
        // stays single no matter how many modules come to depend on it.
        services.AddScoped<IStockPosting, StockPosting>();
        services.AddScoped<IStockQuery, StockQuery>();
        services.AddScoped<IStockAvailability, StockAvailability>();
        services.AddScoped<IStockReservations, StockReservations>();
        services.AddScoped<IStockCounts, StockCounts>();

        // Phase 6's, and the one that finally cashes the cheque ADR 0001 wrote in
        // Phase 2. Everything above answers a question about now; this answers the
        // same questions about then, by folding the ledger forward rather than by
        // remembering anything — which is only possible because the ledger is
        // complete and immutable.
        services.AddScoped<IStockHistory, StockHistory>();

        // TryAdd, because a clock is host-wide and every module that posts
        // timestamps will want the same one — including a test host that has
        // already substituted a fake.
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ReconciliationOptions>()
            .Bind(configuration.GetSection(ReconciliationOptions.SectionName))
            .ValidateDataAnnotations();

        // Hosted services registered by a module are unusual, and this one earns
        // it: the drift alarm is part of what makes ADR 0001 a checkable claim
        // rather than an intention, and it belongs to the module that owns the
        // invariant. It is read-only, so running on every API replica is safe.
        services.AddHostedService<PeriodicReconciliation>();

        // No seeder. There is nothing to seed — the ledger starts empty, and the
        // virtual counterparty locations it posts against are MasterData's.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ReceiveStock.Map(endpoints);
        TransferStock.Map(endpoints);
        AdjustStock.Map(endpoints);
        ScrapStock.Map(endpoints);
        ChangeStockStatus.Map(endpoints);
        ReverseMovement.Map(endpoints);
        GetStockBalances.Map(endpoints);
        GetStockMovements.Map(endpoints);

        // The integration surface's read (Phase 7). Beside the movements enquiry
        // rather than inside it: same rows, opposite direction, different contract.
        GetLedgerChanges.Map(endpoints);
        RunReconciliation.Map(endpoints);
        GetReconciliationHistory.Map(endpoints);

        // The accuracy reads. Both are Inventory's because the data is — counts and
        // commitments both live in this schema — and the console composes them with
        // Work's override rates into one screen rather than any module growing a
        // cross-boundary query to build the dashboard server-side.
        GetCountAccuracy.Map(endpoints);
        GetStrandedReservations.Map(endpoints);
    }
}
