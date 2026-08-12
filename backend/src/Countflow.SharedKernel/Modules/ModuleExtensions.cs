using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.SharedKernel.Modules;

public static class ModuleExtensions
{
    /// <summary>
    /// Registers a module's DbContext against its own schema.
    /// <para>
    /// The migrations history table is placed inside the module's schema rather
    /// than the default public one — without this every module would share a
    /// single __EFMigrationsHistory table and their migrations would collide.
    /// </para>
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        string schema)
        where TContext : DbContext, IModuleDbContext
    {
        services.AddDbContext<TContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
            });
        });

        // Also expose it as IModuleDbContext so the migrator can enumerate every
        // module context without taking a reference on each module by name.
        services.AddScoped<IModuleDbContext>(sp => sp.GetRequiredService<TContext>());

        return services;
    }

    /// <summary>
    /// Registers each module's services. Modules are listed explicitly by the
    /// host rather than discovered by assembly scanning: it is faster, greppable,
    /// and makes the dependency direction obvious.
    /// </summary>
    public static IServiceCollection AddCountflowModules(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<ICountflowModule> modules)
    {
        foreach (var module in modules)
        {
            services.AddSingleton(module);
            module.RegisterServices(services, configuration);
        }

        return services;
    }

    /// <summary>
    /// Mounts every module's endpoints under the published API prefix.
    /// <para>
    /// The one place the surface's version appears. Modules map relative paths and
    /// are handed the versioned group rather than the raw route builder, so no
    /// endpoint author can put an endpoint outside the version — and adding a v2
    /// beside v1 is a second call here rather than an edit to every module. See
    /// <see cref="ApiSurface"/>.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapCountflowModules(this IEndpointRouteBuilder endpoints)
    {
        var surface = endpoints.MapGroup(ApiSurface.Prefix);

        foreach (var module in endpoints.ServiceProvider.GetServices<ICountflowModule>())
        {
            module.MapEndpoints(surface);
        }

        return endpoints;
    }
}
