using Countflow.Bootstrap;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// Countflow migrator.
//
// Runs every module's migrations, then every module's seeders, then exits.
// Deliberately a separate process rather than a startup hook in the API:
//   * two API replicas booting together would otherwise race on the same
//     migration, and EF's history table is not a distributed lock;
//   * a deploy that only restarts the API should not silently change schema;
//   * it keeps the API image free of design-time tooling.
//
// Runs to completion before the API starts (compose depends_on:
// service_completed_successfully). Exit code 0 means the database is ready.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCountflowModules(builder.Configuration, ModuleCatalog.All);

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var configuration = host.Services.GetRequiredService<IConfiguration>();

try
{
    await WaitForDatabaseAsync(configuration, logger, CancellationToken.None);

    await using var scope = host.Services.CreateAsyncScope();

    var contexts = scope.ServiceProvider.GetServices<IModuleDbContext>().ToList();
    logger.LogInformation("Migrating {Count} module schema(s)", contexts.Count);

    foreach (var context in contexts)
    {
        var name = context.GetType().Name;
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("{Context}: already up to date", name);
            continue;
        }

        logger.LogInformation("{Context}: applying {Count} migration(s): {Migrations}", name, pending.Count, string.Join(", ", pending));
        await context.Database.MigrateAsync();
    }

    var seeders = scope.ServiceProvider.GetServices<IModuleSeeder>().OrderBy(s => s.Order).ToList();
    logger.LogInformation("Running {Count} seeder(s)", seeders.Count);

    foreach (var seeder in seeders)
    {
        await seeder.SeedAsync(CancellationToken.None);
    }

    logger.LogInformation("Migration complete");
    return 0;
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Migration failed");
    return 1;
}

// The compose healthcheck already gates on Postgres accepting connections, but
// the migrator is also run by hand and in CI where nothing gates it.
static async Task WaitForDatabaseAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("Countflow")
        ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

    const int maxAttempts = 30;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            logger.LogInformation("Database reachable after {Attempts} attempt(s)", attempt);
            return;
        }
        catch (NpgsqlException) when (attempt < maxAttempts)
        {
            logger.LogInformation("Database not ready (attempt {Attempt}/{Max}), retrying in 2s", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    throw new InvalidOperationException($"Database was not reachable after {maxAttempts} attempts.");
}

/// <summary>Named so EF Core design-time tooling can use this as the startup project.</summary>
public partial class Program;
