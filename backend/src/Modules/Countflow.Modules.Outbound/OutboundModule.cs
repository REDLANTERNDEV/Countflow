using Countflow.Modules.Outbound.Features.Orders;
using Countflow.Modules.Outbound.Features.Returns;
using Countflow.Modules.Outbound.Features.Waves;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Outbound;

/// <summary>
/// The outbound loop: what a customer asked for, which stock was promised to it,
/// and the work that gets it out of the door.
/// <para>
/// An orchestrator, exactly as Inbound is. It owns orders, waves and returns and
/// nothing else — the stock is chosen by Rules, held and moved by Inventory, and fetched
/// by Work, each through a published contract. Phase 4 was allowed to extend the
/// ledger no more than Phase 3 was, and this module is where that discipline shows:
/// it takes a great deal of stock out of the building and contains no code that
/// writes to it.
/// </para>
/// <para>
/// Publishes nothing. Like the inbound loop, its output is stock and work, and
/// both of those are already somebody else's surface.
/// </para>
/// </summary>
public sealed class OutboundModule : ICountflowModule
{
    public string Name => "Outbound";

    public string Schema => OutboundDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<OutboundDbContext>(connectionString, Schema);
        services.AddScoped<IModuleSeeder, OutboundSeeder>();

        services.AddScoped<OrderDescriber>();
        services.AddScoped<ReturnDescriber>();

        // Releasing is a service rather than an endpoint body because a wave
        // releases its orders, and a wave that did it through a second path would
        // be a second definition of what "released" means.
        services.AddScoped<OrderRelease>();
        services.AddScoped<OrderRepair>();

        services.TryAddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetOrders.Map(endpoints);
        GetOrder.Map(endpoints);
        CreateOrder.Map(endpoints);
        ReleaseOrder.Map(endpoints);
        RepairOrderLine.Map(endpoints);
        PackOrder.Map(endpoints);
        DespatchOrder.Map(endpoints);
        CancelOrder.Map(endpoints);

        GetReturns.Map(endpoints);
        GetReturn.Map(endpoints);
        BookReturn.Map(endpoints);
        DispositionReturnLine.Map(endpoints);

        GetWaves.Map(endpoints);
        CreateWave.Map(endpoints);
        ReleaseWave.Map(endpoints);
    }
}
