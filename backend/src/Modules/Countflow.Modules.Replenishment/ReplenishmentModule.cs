using Countflow.Modules.Replenishment.Features;
using Countflow.Modules.Replenishment.Persistence;
using Countflow.SharedKernel.Modules;
using Countflow.SharedKernel.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.Modules.Replenishment;

public sealed class ReplenishmentModule : ICountflowModule
{
    public string Name => "Replenishment";
    public string Schema => ReplenishmentDbContext.SchemaName;
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");
        services.AddModuleDbContext<ReplenishmentDbContext>(connection, Schema);
        services.AddScoped<ReplenishmentRunner>();
        services.AddScoped<ReplenishmentDescriber>();
        services.AddScoped<IBackgroundJobCatalog, ReplenishmentCatalog>();
    }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => ReplenishmentEndpoints.Map(endpoints);
}
