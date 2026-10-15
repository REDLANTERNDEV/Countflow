using Countflow.Modules.Webhooks.Features;
using Countflow.Modules.Webhooks.Persistence;
using Countflow.Modules.Webhooks.Transport;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Countflow.Modules.Webhooks;

public sealed class WebhooksModule : ICountflowModule
{
    public string Name => "Webhooks";
    public string Schema => WebhooksDbContext.SchemaName;
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WebhooksDbContext>(configuration.GetConnectionString("Countflow") ?? throw new InvalidOperationException("Connection string 'Countflow' missing."), Schema);
        services.AddScoped<WebhookRunner>(); services.AddScoped<IBackgroundJobCatalog, WebhookCatalog>();
        services.AddSingleton<WebhookSecrets>(); services.AddSingleton<ReceiverPolicy>(); services.AddSingleton<IWebhookTransport, WebhookTransport>();
    }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => WebhookEndpoints.Map(endpoints);
}
