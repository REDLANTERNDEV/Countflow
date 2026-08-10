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

    public static IEndpointRouteBuilder MapCountflowModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var module in endpoints.ServiceProvider.GetServices<ICountflowModule>())
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
