using Countflow.Modules.Jobs.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Jobs;

public sealed class JobsModule : ICountflowModule
{
    public string Name => "Jobs";
    public string Schema => JobsDbContext.SchemaName;
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");
        services.AddModuleDbContext<JobsDbContext>(connection, Schema);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<JobRunnerOptions>().Bind(configuration.GetSection("Jobs"))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddHostedService<BackgroundJobWorker>();
    }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
