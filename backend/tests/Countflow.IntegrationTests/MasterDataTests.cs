using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Exercises master data and the scan seam against the real schema, with a
/// realistic layout loaded — the collisions that matter (two zones sharing a
/// code, GTINs across packaging levels) only appear at that scale.
/// </summary>
public sealed class MasterDataTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const char GS = '';

    private const string AdminSecret = "Countflow#2026";

    /// <summary>Builds a login payload; see the note in <see cref="AuthenticationTests"/>.</summary>
    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private string _accessToken = string.Empty;
    private string _caseGtin = string.Empty;
    private string _sku = string.Empty;

    public async Task InitializeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

            if (!await dbContext.Warehouses.AnyAsync())
            {
                var layout = new WarehouseLayoutBuilder().Build();
                dbContext.Warehouses.Add(layout.Warehouse);
                dbContext.Areas.AddRange(layout.Areas);
                dbContext.Zones.AddRange(layout.Zones);
                dbContext.Locations.AddRange(layout.Locations);
                dbContext.Items.AddRange(new ItemCatalogueBuilder(itemCount: 25).Build());
                await dbContext.SaveChangesAsync();
            }

            var pack = await dbContext.PackConfigurations
                .Include(p => p.Item)
                .FirstAsync(p => p.Level == PackagingLevel.Case && p.Gtin != null);

            _caseGtin = pack.Gtin!;
            _sku = pack.Item!.Sku;
        }

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            Credentials("admin", AdminSecret));

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The demo seeder is off in tests, so create the account we need.
            await CreateAdminAsync();
            response = await client.PostAsJsonAsync("/api/v1/auth/login",
                Credentials("admin", AdminSecret));
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginBody>();
        _accessToken = body!.AccessToken;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record LoginBody(string AccessToken);

    private async Task CreateAdminAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Modules.Identity.Domain.CountflowUser>>();

        var user = new Modules.Identity.Domain.CountflowUser
        {
            UserName = "admin",
            Email = "admin@countflow.test",
            EmailConfirmed = true,
            DisplayName = "Test Admin",
            OperatorCode = "ADM-TEST"
        };

        await users.CreateAsync(user, AdminSecret);
    }

    private HttpClient AuthenticatedClient()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return client;
    }

    [Fact]
    public async Task Virtual_counterparty_locations_exist_for_the_ledger()
    {
        using var client = AuthenticatedClient();

        // Phase 2's double-entry ledger cannot post a receipt or a despatch
        // without these, so their absence must fail loudly here rather than
        // halfway through building the ledger.
        foreach (var type in new[] { "Supplier", "Customer", "Adjustment", "Scrap", "InTransit" })
        {
            var response = await client.GetFromJsonAsync<PagedLocations>($"/api/v1/locations?type={type}");
            Assert.NotNull(response);
            Assert.Equal(1, response.Total);
        }
    }

    [Fact]
    public async Task Pick_faces_are_ordered_for_a_serpentine_walk()
    {
        using var client = AuthenticatedClient();

        var page = await client.GetFromJsonAsync<PagedLocations>(
            "/api/v1/locations?type=PickFace&pageSize=200");

        Assert.NotNull(page);
        Assert.True(page.Total > 100);

        // Pick sequence is what makes a pick list walk one direction instead of
        // doubling back, so it must be monotonic in the order the API returns.
        var sequences = page.Items.Select(l => l.PickSequence).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    [Fact]
    public async Task Item_detail_includes_the_full_packaging_hierarchy()
    {
        using var client = AuthenticatedClient();

        var item = await client.GetFromJsonAsync<ItemBody>($"/api/v1/items/{_sku}");

        Assert.NotNull(item);

        // Regression guard: a client-evaluated projection silently returned an
        // empty collection here, with no error at all.
        Assert.NotEmpty(item.PackConfigurations);
        Assert.Contains(item.PackConfigurations, p => p.Level == "Case");
        Assert.Contains(item.PackConfigurations, p => p.Level == "Each" && p.BaseUnitQuantity == 1);
    }

    [Fact]
    public async Task Every_seeded_gtin_carries_a_valid_check_digit()
    {
        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var gtins = await dbContext.PackConfigurations
            .Where(p => p.Gtin != null)
            .Select(p => p.Gtin!)
            .ToListAsync();

        Assert.NotEmpty(gtins);

        // Fake barcodes that fail their own check digit would make the demo lie
        // about whether scanning works.
        Assert.All(gtins, gtin => Assert.True(Gtin.HasValidCheckDigit(gtin), $"Invalid GTIN {gtin}"));
    }

    [Fact]
    public async Task A_case_scan_resolves_to_the_item_and_converts_to_base_units()
    {
        using var client = AuthenticatedClient();

        var barcode = $"01{_caseGtin}10LOT-4711{GS}1727011537000012";
        var response = await client.PostAsJsonAsync("/api/v1/scan/resolve", new { barcode });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scan = await response.Content.ReadFromJsonAsync<ScanBody>();

        Assert.NotNull(scan);
        Assert.True(scan.ItemRecognised);
        Assert.Equal("Case", scan.Item!.PackagingLevel);
        Assert.Equal("LOT-4711", scan.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 15), scan.ExpiryDate);

        // 12 cases of N units each must reach the ledger as base units.
        Assert.Equal(12 * scan.Item.BaseUnitQuantity, scan.BaseUnitQuantity);
    }

    [Fact]
    public async Task An_unknown_gtin_is_reported_rather_than_guessed_at()
    {
        using var client = AuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/scan/resolve",
            new { barcode = "0109501101530003" });

        var scan = await response.Content.ReadFromJsonAsync<ScanBody>();

        Assert.NotNull(scan);
        Assert.False(scan.ItemRecognised);
        // Asserting the code rather than the wording: the API returns codes so
        // the client can render Turkish, and a test tied to English prose would
        // break the moment the catalogue is reworded.
        var warning = Assert.Single(scan.Warnings);
        Assert.Equal("gtin_not_found", warning.Code);
        Assert.Equal("09501101530003", warning.Args["gtin"]);
    }

    [Fact]
    public async Task A_malformed_barcode_is_a_bad_request_with_a_coded_error()
    {
        using var client = AuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/scan/resolve",
            new { barcode = $"01{_caseGtin}17279999" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("gs1.invalid_date", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_dry_run_reports_issues_and_writes_nothing()
    {
        using var client = AuthenticatedClient();

        const string csv = """
            Sku,Description,AbcClass,CaseGtin,CaseQuantity
            TEST-9001,Valid row,A,,12
            TEST-9002,Bad barcode,A,05098769000099,12
            TEST-9001,Duplicate sku,B,,6
            """;

        var response = await client.PostAsync("/api/v1/import/items?dryRun=true",
            new StringContent(csv, Encoding.UTF8, "text/csv"));

        var result = await response.Content.ReadFromJsonAsync<ImportBody>();

        Assert.NotNull(result);
        Assert.False(result.Committed);
        Assert.Contains(result.Issues, i => i.Field == "CaseGtin");
        Assert.Contains(result.Issues, i => i.Field == "Sku");

        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        Assert.False(await dbContext.Items.AnyAsync(i => i.Sku.StartsWith("TEST-90")));
    }

    [Fact]
    public async Task Import_refuses_to_commit_a_file_with_any_bad_row()
    {
        using var client = AuthenticatedClient();

        const string csv = """
            Sku,Description,AbcClass,CaseGtin,CaseQuantity
            TEST-9101,Perfectly fine,A,,12
            TEST-9102,Bad barcode,A,05098769000099,12
            """;

        var response = await client.PostAsync("/api/v1/import/items?dryRun=false",
            new StringContent(csv, Encoding.UTF8, "text/csv"));

        var result = await response.Content.ReadFromJsonAsync<ImportBody>();
        Assert.NotNull(result);
        Assert.False(result.Committed);

        // All or nothing: the good row must not land either, so the operator
        // always knows the database matches the last file that fully validated.
        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        Assert.False(await dbContext.Items.AnyAsync(i => i.Sku == "TEST-9101"));
    }

    [Fact]
    public async Task Importing_the_same_clean_file_twice_creates_no_duplicates()
    {
        using var client = AuthenticatedClient();

        const string csv = """
            Sku,Description,AbcClass,CaseQuantity
            TEST-9201,Idempotent row,B,24
            """;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await client.PostAsync("/api/v1/import/items?dryRun=false",
                new StringContent(csv, Encoding.UTF8, "text/csv"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        Assert.Equal(1, await dbContext.Items.CountAsync(i => i.Sku == "TEST-9201"));
    }

    private sealed record PagedLocations(List<LocationBody> Items, int Total);

    private sealed record LocationBody(string Code, string Type, int PickSequence);

    private sealed record ItemBody(string Sku, List<PackBody> PackConfigurations);

    private sealed record PackBody(string Level, int BaseUnitQuantity, string? Gtin);

    private sealed record ScanBody(
        bool ItemRecognised,
        ScanItemBody? Item,
        string? LotNumber,
        DateOnly? ExpiryDate,
        int? BaseUnitQuantity,
        List<ScanWarningBody> Warnings);

    private sealed record ScanWarningBody(string Code, Dictionary<string, string> Args);

    private sealed record ScanItemBody(string Sku, string PackagingLevel, int BaseUnitQuantity);

    private sealed record ImportBody(bool Committed, int RowsRead, List<IssueBody> Issues);

    private sealed record IssueBody(int Row, string Field, string Message);
}
