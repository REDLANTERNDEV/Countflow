using System.Net;
using Countflow.Bootstrap;
using Countflow.Modules.Identity.Persistence;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Countflow.IntegrationTests;

/// <summary>
/// Guards the architectural rules from ADR 0002. These are cheap to write now
/// and very expensive to reinstate once several modules have quietly drifted.
/// </summary>
public sealed class ModuleIsolationTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public void Every_module_declares_a_distinct_schema()
    {
        var schemas = ModuleCatalog.All.Select(module => module.Schema).ToList();

        Assert.All(schemas, schema => Assert.False(string.IsNullOrWhiteSpace(schema)));
        Assert.Equal(schemas.Count, schemas.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_virtual_location_type_is_reachable_through_the_published_contract()
    {
        // Inventory never sees LocationType — it only knows CounterpartyKind, which
        // is what keeps MasterData's topology vocabulary inside MasterData. The
        // cost of that boundary is this pair of enums, and a new virtual location
        // type that nobody added to the contract would be silently treated as a
        // shelf by the ledger, including being forbidden to go negative. That would
        // not fail; it would just quietly refuse every receipt through it.
        var virtualTypes = Enum.GetValues<LocationType>().Where(type => type.IsVirtual()).ToList();

        Assert.NotEmpty(virtualTypes);

        Assert.All(virtualTypes, type => Assert.True(
            Enum.TryParse<CounterpartyKind>(type.ToString(), out var kind) && kind != CounterpartyKind.None,
            $"Virtual location type {type} has no CounterpartyKind, so the ledger cannot post against it."));

        // And the other direction, which catches a counterparty invented in the
        // contract that no location can ever satisfy.
        var counterparties = Enum.GetValues<CounterpartyKind>()
            .Where(kind => kind != CounterpartyKind.None);

        Assert.All(counterparties, kind => Assert.True(
            Enum.TryParse<LocationType>(kind.ToString(), out var type) && type.IsVirtual(),
            $"CounterpartyKind {kind} has no virtual location type behind it."));
    }

    [Fact]
    public void Every_module_context_is_resolvable_through_the_marker_interface()
    {
        using var scope = fixture.Services.CreateScope();

        var contexts = scope.ServiceProvider.GetServices<IModuleDbContext>().ToList();

        // If this drifts, the migrator silently stops migrating a module.
        Assert.Equal(ModuleCatalog.All.Count, contexts.Count);
    }

    [Fact]
    public async Task Each_module_keeps_its_tables_in_its_own_schema()
    {
        using var scope = fixture.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityModuleDbContext>();

        await using var connection = new NpgsqlConnection(identity.Database.GetConnectionString());
        await connection.OpenAsync();

        var schemas = await ReadSchemasAsync(connection);

        Assert.Contains(IdentityModuleDbContext.SchemaName, schemas);
        Assert.Contains(MasterDataDbContext.SchemaName, schemas);
        Assert.Contains(InventoryDbContext.SchemaName, schemas);

        // Nothing belongs in public — that is the shared namespace a module
        // boundary violation would land in.
        var publicTables = await ReadTableCountAsync(connection, "public");
        Assert.Equal(0, publicTables);
    }

    [Fact]
    public async Task Each_module_owns_its_migrations_history_table()
    {
        using var scope = fixture.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityModuleDbContext>();

        await using var connection = new NpgsqlConnection(identity.Database.GetConnectionString());
        await connection.OpenAsync();

        // A single shared history table would make two modules' migrations
        // collide, so each schema must carry its own.
        foreach (var schema in new[]
                 {
                     IdentityModuleDbContext.SchemaName,
                     MasterDataDbContext.SchemaName,
                     InventoryDbContext.SchemaName
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = @schema AND table_name = '__EFMigrationsHistory'
                """;
            command.Parameters.AddWithValue("schema", schema);

            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task Health_endpoints_report_the_stack_as_ready()
    {
        var client = fixture.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_the_module_endpoints()
    {
        var client = fixture.CreateClient();

        var document = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/auth/login", document, StringComparison.Ordinal);
        Assert.Contains("/api/warehouses", document, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ReadSchemasAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_name FROM information_schema.schemata";

        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    private static async Task<int> ReadTableCountAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema";
        command.Parameters.AddWithValue("schema", schema);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
