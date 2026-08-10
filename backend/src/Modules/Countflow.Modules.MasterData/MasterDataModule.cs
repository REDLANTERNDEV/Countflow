using Countflow.Modules.MasterData.Features.Import;
using Countflow.Modules.MasterData.Features.Items;
using Countflow.Modules.MasterData.Features.Locations;
using Countflow.Modules.MasterData.Features.Scanning;
using Countflow.Modules.MasterData.Features.Warehouses;
using Countflow.Modules.MasterData.Persistence;
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
