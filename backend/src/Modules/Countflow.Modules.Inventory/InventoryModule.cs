using Countflow.Modules.Inventory.Features.Adjustments;
using Countflow.Modules.Inventory.Features.Enquiry;
using Countflow.Modules.Inventory.Features.Receipts;
using Countflow.Modules.Inventory.Features.Reconciliation;
using Countflow.Modules.Inventory.Features.Reversals;
using Countflow.Modules.Inventory.Features.StatusChanges;
using Countflow.Modules.Inventory.Features.Transfers;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
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
        ChangeStockStatus.Map(endpoints);
        ReverseMovement.Map(endpoints);
        GetStockBalances.Map(endpoints);
        GetStockMovements.Map(endpoints);
        RunReconciliation.Map(endpoints);
    }
}
