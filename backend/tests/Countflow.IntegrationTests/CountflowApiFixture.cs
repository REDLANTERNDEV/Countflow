using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Countflow.IntegrationTests;

/// <summary>
/// Boots the real API against a real PostgreSQL container.
/// <para>
/// Deliberately not the in-memory provider: schema-per-module, unique indexes
/// and (from Phase 2) row locking during allocation all behave differently
/// there, so an in-memory pass would prove nothing about the ledger.
/// </para>
/// </summary>
public sealed class CountflowApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("countflow")
        .WithUsername("countflow")
        .WithPassword("countflow")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgres.StartAsync();

        // Apply migrations and then run the seeders through the module contexts,
        // exactly as the migrator does in production, so the tests exercise the
        // real path.
        using var scope = Services.CreateScope();
        foreach (var context in scope.ServiceProvider.GetServices<IModuleDbContext>())
        {
            await context.Database.MigrateAsync();
        }

        // Seeding is not optional here. Demo data stays off — Seed:DemoData is
        // unset — but reference data a module cannot run without is exactly what
        // seeders are for, and from Phase 3 that includes the putaway rule set. A
        // fixture that skipped them would test a warehouse with no rules, which is
        // a configuration production can never be in.
        foreach (var seeder in scope.ServiceProvider.GetServices<IModuleSeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(CancellationToken.None);
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // UseSetting rather than ConfigureAppConfiguration: under minimal hosting
        // the appsettings.json sources are appended after the factory's
        // ConfigureAppConfiguration callbacks, so an in-memory connection string
        // loses to the file default and the tests quietly hit localhost:5432.
        // UseSetting writes into host configuration, which wins.
        builder.UseSetting("ConnectionStrings:Countflow", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:Issuer", "countflow-tests");
        builder.UseSetting("Jwt:Audience", "countflow-tests");
        builder.UseSetting("Jwt:SigningKey", "countflow-integration-test-signing-key-32-bytes-min");
        builder.UseSetting("Observability:SeqUrl", string.Empty);
        builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
    }
}
