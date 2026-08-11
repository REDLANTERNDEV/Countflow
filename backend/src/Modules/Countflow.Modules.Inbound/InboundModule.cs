using Countflow.Modules.Inbound.Features.Asns;
using Countflow.Modules.Inbound.Features.Receiving;
using Countflow.Modules.Inbound.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Inbound;

/// <summary>
/// The inbound loop: what is expected, what actually arrived, and the work that
/// gets it off the dock.
/// <para>
/// An orchestrator. It owns advice notes and nothing else — the receipt is posted
/// by Inventory, the destination is chosen by Rules, the job is queued by Work,
/// and each of those is reached through a published contract. Phase 3 was
/// explicitly not allowed to extend the ledger, and this module is where that
/// discipline is visible: it moves a great deal of stock and contains no code
/// that writes to it.
/// </para>
/// </summary>
public sealed class InboundModule : ICountflowModule
{
    public string Name => "Inbound";

    public string Schema => InboundDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<InboundDbContext>(connectionString, Schema);
        services.AddScoped<IModuleSeeder, InboundSeeder>();

        services.TryAddSingleton(TimeProvider.System);

        // Nothing published. No other module has any business knowing what is on a
        // truck — the inbound loop's output is stock and work, and both of those
        // are already somebody else's published surface.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetAsns.Map(endpoints);
        CreateAsn.Map(endpoints);
        ReceiveAtDock.Map(endpoints);
    }
}
