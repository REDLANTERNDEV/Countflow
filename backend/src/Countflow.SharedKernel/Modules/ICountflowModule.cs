using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.SharedKernel.Modules;

/// <summary>
/// A vertical module of the Countflow monolith. Each module owns exactly one
/// PostgreSQL schema and reaches other modules only through their published
/// interfaces — never through their tables.
/// </summary>
public interface ICountflowModule
{
    /// <summary>Human-readable module name, used in logs and health reporting.</summary>
    string Name { get; }

    /// <summary>
    /// The PostgreSQL schema this module owns. Nothing outside the module may
    /// write to it, and its migration history lives here rather than in public.
    /// </summary>
    string Schema { get; }

    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
