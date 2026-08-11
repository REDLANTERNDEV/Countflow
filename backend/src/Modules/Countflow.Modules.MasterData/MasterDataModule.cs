using Countflow.Modules.MasterData.Features.Import;
using Countflow.Modules.MasterData.Integration;
using Countflow.Modules.MasterData.Features.Items;
using Countflow.Modules.MasterData.Features.Locations;
using Countflow.Modules.MasterData.Features.Scanning;
using Countflow.Modules.MasterData.Features.Warehouses;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.Modules.MasterData;

public sealed class MasterDataModule : ICountflowModule
{
    public string Name => "MasterData";

    public string Schema => MasterDataDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<MasterDataDbContext>(connectionString, Schema);
        services.AddScoped<IModuleSeeder, MasterDataSeeder>();

        // The published boundary (ADR 0002). Registered against the contract
        // interfaces only — nothing outside this module resolves
        // MasterDataDbContext, and the Inventory project cannot even name it.
        services.AddScoped<IItemDirectory, ItemDirectory>();
        services.AddScoped<ILocationDirectory, LocationDirectory>();
        services.AddScoped<IWarehouseDirectory, WarehouseDirectory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetWarehouses.Map(endpoints);
        GetLocations.Map(endpoints);
        GetItems.Map(endpoints);
        ResolveScan.Map(endpoints);
        ImportItems.Map(endpoints);
        ImportLocations.Map(endpoints);
    }
}
