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

    /// <summary>
    /// Maps this module's endpoints.
    /// <para>
    /// <b>Paths here are relative to the published surface</b> — write
    /// <c>/inventory/movements</c>, not <c>/api/v1/inventory/movements</c>. The
    /// builder handed in is already mounted under <see cref="ApiSurface.Prefix"/>,
    /// so the version is the host's business and cannot be got wrong one endpoint
    /// at a time.
    /// </para>
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
