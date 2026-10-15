using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Countflow.Modules.Work.Persistence;

namespace Countflow.IntegrationTests;

public sealed class BlindBinCountTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public async Task Starting_a_count_waits_for_earlier_uncommitted_movements_even_when_a_later_sequence_is_visible()
    {
        var site = await SiteAsync(); Guid secondItem;
        using (var scope = fixture.Services.CreateScope())
        {
            var masters = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var item = new Item { Sku = site.Sku + "-B", Description = "Second holding", BaseUom = "EA" };
            masters.Items.Add(item); await masters.SaveChangesAsync(); secondItem = item.Id;
        }
        var posted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PendingReceipt()
        {
            using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var result = await scope.ServiceProvider.GetRequiredService<StockLedger>().PostAsync(new PostingCommand {
                    ItemId = site.Item, Quantity = 1, From = new Holding(site.Supplier, null, InventoryStatus.Available),
                    To = new Holding(site.Bin, null, InventoryStatus.Available), Type = MovementType.Receipt, Reason = MovementReason.PurchaseOrderReceipt,
                    IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = new LedgerActor(Guid.CreateVersion7(), "bin-test") }, CancellationToken.None);
                Assert.True(result.IsSuccess); posted.TrySetResult(result.Value.Sequence);
                await finish.Task; await transaction.CommitAsync();
            });
        }
        var pending = PendingReceipt();
        try
        {
            var earlier = await posted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ReceiptAsync(site with { Item = secondItem, Sku = site.Sku + "-B" }, 2);
            using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.OpenConnectionAsync();
            var later = await db.StockMovements.Where(movement => movement.ItemId == secondItem).Select(movement => movement.Sequence).SingleAsync();
            Assert.True(later > earlier);
            var baseline = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>().ReadVersionAsync(site.Bin, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(baseline.IsCompleted, "A visible later sequence cannot hide an earlier movement still committing.");
            finish.TrySetResult(); await pending;
            Assert.Equal(later, await baseline.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { finish.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task A_null_observation_is_refused_as_validation_before_any_write()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IBlindBinCounting>()
            .ConfirmAsync(Instruction(site, 0, [null!]), CancellationToken.None);
        Assert.True(result.IsFailure); Assert.Equal("inventory.bin_count_invalid", result.Error.Code);
    }

    [Fact]
    public async Task The_maximum_accepted_reason_is_preserved_with_supervisor_provenance()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope(); using var supervisor = await ClientAsync(Roles.Supervisor);
        var bin = await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindAsync(site.Bin, CancellationToken.None);
        var reason = new string('R', 120);
        var raised = await PostAsync(supervisor, "/api/v1/work/bin-counts", new { locationCode = bin!.Code, reason });
        Assert.True(raised.IsSuccessStatusCode, await raised.Content.ReadAsStringAsync());
        var body = await raised.Content.ReadFromJsonAsync<JsonElement>(); Assert.EndsWith(reason, body.GetProperty("sourceReference").GetString());
    }

    [Theory]
    [InlineData("0.00001")]
    [InlineData("3.14159")]
    [InlineData("100000000000000")]
    public async Task Unsupported_quantity_precision_or_magnitude_is_refused_before_any_write(string raw)
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var quantity = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var result = await scope.ServiceProvider.GetRequiredService<IBlindBinCounting>().ConfirmAsync(Instruction(site, 0, [new(site.Sku, quantity)]), CancellationToken.None);
        Assert.True(result.IsFailure); Assert.Equal("inventory.bin_count_invalid", result.Error.Code);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.False(await db.StockMovements.AnyAsync(movement => movement.ItemId == site.Item));
        Assert.False(await db.StockCounts.AnyAsync(count => count.ItemId == site.Item));
        Assert.False(await db.BinCounts.AnyAsync(count => count.LocationId == site.Bin));
    }

    [Fact]
    public async Task Supported_fractional_units_match_the_projection_and_immutable_agreement_receipt()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var found = await counter.ConfirmAsync(Instruction(site, 0, [new(site.Sku, 3.1415m)]), CancellationToken.None);
        Assert.True(found.IsSuccess); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(3.1415m, await db.StockBalances.Where(b => b.LocationId == site.Bin && b.ItemId == site.Item).Select(b => b.Quantity).SingleAsync());
        var agreement = await counter.ConfirmAsync(Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None), [new(site.Sku, 3.1415m)]), CancellationToken.None);
        Assert.True(agreement.IsSuccess); Assert.Equal(0m, Assert.Single(agreement.Value.Counts).Variance);
        Assert.Equal(3.1415m, Assert.Single(agreement.Value.Counts).CountedQuantity);
    }

    [Fact]
    public async Task A_recorded_confirmation_cannot_be_released_during_task_completion_recovery()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var bin = await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindAsync(site.Bin, CancellationToken.None);
        using var supervisor = await ClientAsync(Roles.Supervisor); using var worker = await ClientAsync(Roles.Operator);
        var raised = await PostAsync(supervisor, "/api/v1/work/bin-counts", new { locationCode = bin!.Code, reason = "Recovery audit" });
        var id = (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var claimed = await PostAsync(worker, $"/api/v1/work/tasks/{id}/claim", new { }); claimed.EnsureSuccessStatusCode();
        var task = await scope.ServiceProvider.GetRequiredService<WorkDbContext>().WorkTasks.AsNoTracking().SingleAsync(task => task.Id == id);
        var input = new BinCountInstruction(id, site.Bin, task.BinCountVersion!.Value, [new(site.Sku, 4)], Guid.CreateVersion7().ToString(),
            new PostingActor(task.AssignedToUserId!.Value, task.AssignedToUserName!), task.SourceReference!);
        var recorded = await scope.ServiceProvider.GetRequiredService<IBlindBinCounting>().ConfirmAsync(input, CancellationToken.None);
        Assert.True(recorded.IsSuccess);
        var release = await PostAsync(worker, $"/api/v1/work/tasks/{id}/release", new { });
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);
        var restart = await PostAsync(worker, $"/api/v1/work/tasks/{id}/bin-count/start", new { restart = true });
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        var recovered = await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", new { toLocationCode = bin.Code,
            binCountVersion = task.BinCountVersion, binCountClaimId = task.BinCountClaimId, observed = input.Observed }, input.IdempotencyKey);
        Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Release_reclaim_and_recount_invalidate_old_cached_confirmations()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var bin = await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindAsync(site.Bin, CancellationToken.None);
        using var supervisor = await ClientAsync(Roles.Supervisor); using var worker = await ClientAsync(Roles.Operator);
        var raised = await PostAsync(supervisor, "/api/v1/work/bin-counts", new { locationCode = bin!.Code, reason = "Claim fence audit" });
        var id = (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        async Task<JsonElement> Claim() => (await (await PostAsync(worker, $"/api/v1/work/tasks/{id}/claim", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task");
        object Body(JsonElement task) => new { toLocationCode = bin.Code, binCountVersion = task.GetProperty("binCountVersion").GetInt64(),
            binCountClaimId = task.GetProperty("binCountClaimId").GetGuid(), observed = Array.Empty<BinCountObservation>() };
        var original = await Claim(); (await PostAsync(worker, $"/api/v1/work/tasks/{id}/release", new { })).EnsureSuccessStatusCode();
        var reclaimed = await Claim(); Assert.NotEqual(original.GetProperty("binCountClaimId"), reclaimed.GetProperty("binCountClaimId"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", Body(original))).StatusCode);
        await ReceiptAsync(site, 3);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", Body(reclaimed))).StatusCode);
        var restart = await PostAsync(worker, $"/api/v1/work/tasks/{id}/bin-count/start", new { restart = true }); restart.EnsureSuccessStatusCode();
        var fresh = (await restart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", Body(reclaimed))).StatusCode);
        (await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", Body(fresh))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Supervisor_raises_location_only_work_and_operator_claims_and_completes_a_discovery()
    {
        var site = await SiteAsync(); using var scope = fixture.Services.CreateScope();
        var bin = await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindAsync(site.Bin, CancellationToken.None);
        using var supervisor = await ClientAsync(Roles.Supervisor); using var worker = await ClientAsync(Roles.Operator);
        using var other = await ClientAsync(Roles.Operator);
        var key = Guid.CreateVersion7().ToString(); var create = new { locationCode = bin!.Code, reason = "Empty-bin audit" };
        var unauthorized = await PostAsync(worker, "/api/v1/work/bin-counts", create, key);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        var raised = await PostAsync(supervisor, "/api/v1/work/bin-counts", create, key);
        Assert.True(raised.IsSuccessStatusCode, await raised.Content.ReadAsStringAsync());
        var task = await raised.Content.ReadFromJsonAsync<JsonElement>(); var id = task.GetProperty("id").GetGuid();
        Assert.Equal("BinCount", task.GetProperty("kind").GetString()); Assert.Equal("", task.GetProperty("sku").GetString());
        Assert.Equal(0m, task.GetProperty("quantity").GetDecimal());
        var retry = await PostAsync(supervisor, "/api/v1/work/bin-counts", create, key);
        Assert.Equal(id, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var claimed = await PostAsync(worker, $"/api/v1/work/tasks/{id}/claim", new { });
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync());
        var detail = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        var version = detail.GetProperty("task").GetProperty("binCountVersion").GetInt64();
        var claimId = detail.GetProperty("task").GetProperty("binCountClaimId").GetGuid();
        var confirm = new { toLocationCode = bin.Code, binCountVersion = version, binCountClaimId = claimId, observed = new[] { new { sku = site.Sku, quantity = 12m } } };
        var foreign = await PostAsync(other, $"/api/v1/work/tasks/{id}/complete", confirm);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        var done = await PostAsync(worker, $"/api/v1/work/tasks/{id}/complete", confirm);
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        Assert.Equal("Completed", (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_blind_count_discovers_unrecorded_stock_and_retries_exactly_after_later_movements()
    {
        var site = await SiteAsync();
        using var scope = fixture.Services.CreateScope(); var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var instruction = Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None), [new(site.Sku, 12)]);
        var result = await counter.ConfirmAsync(instruction, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(12m, Assert.Single(result.Value.Counts).CountedQuantity);
        Assert.Equal(12m, Assert.Single(result.Value.Counts).Variance);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var balance = await db.StockBalances.AsNoTracking().SingleAsync(b => b.LocationId == site.Bin && b.ItemId == site.Item);
        Assert.Equal(12m, balance.Quantity); Assert.Null(balance.FirstReceivedAt);
        await ReceiptAsync(site, 3);
        var replay = await counter.ConfirmAsync(instruction, CancellationToken.None);
        Assert.True(replay.IsSuccess); Assert.Equal(result.Value.Counts, replay.Value.Counts);
        var equivalent = await counter.ConfirmAsync(instruction with { Observed = [new(site.Sku, 12.0000m)] }, CancellationToken.None);
        Assert.True(equivalent.IsSuccess); Assert.Equal(result.Value.Counts, equivalent.Value.Counts);
        Assert.Equal(15m, await db.StockBalances.Where(b => b.LocationId == site.Bin && b.ItemId == site.Item).Select(b => b.Quantity).SingleAsync());
        var changed = await counter.ConfirmAsync(instruction with { Observed = [new(site.Sku, 13)] }, CancellationToken.None);
        Assert.True(changed.IsFailure); Assert.Equal("inventory.idempotency_key_reused", changed.Error.Code);
    }

    [Fact]
    public async Task Empty_confirmation_removes_omitted_stock_and_records_a_truly_empty_bin()
    {
        var site = await SiteAsync(); await ReceiptAsync(site, 8);
        using var scope = fixture.Services.CreateScope(); var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var result = await counter.ConfirmAsync(Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None), []), CancellationToken.None);
        Assert.True(result.IsSuccess); Assert.Equal(-8m, Assert.Single(result.Value.Counts).Variance);
        var empty = await counter.ConfirmAsync(Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None), []), CancellationToken.None);
        Assert.True(empty.IsSuccess); Assert.Empty(empty.Value.Counts);
    }

    [Fact]
    public async Task A_changed_bin_and_an_invalid_second_line_refuse_the_whole_confirmation()
    {
        var site = await SiteAsync();
        using var scope = fixture.Services.CreateScope(); var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var version = await counter.ReadVersionAsync(site.Bin, CancellationToken.None); await ReceiptAsync(site, 4);
        var stale = await counter.ConfirmAsync(Instruction(site, version, [new(site.Sku, 2)]), CancellationToken.None);
        Assert.True(stale.IsFailure); Assert.Equal("inventory.bin_changed", stale.Error.Code);
        var invalid = await counter.ConfirmAsync(Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None),
            [new(site.Sku, 2), new("MISSING-SKU", 5)]), CancellationToken.None);
        Assert.True(invalid.IsFailure);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(4m, await db.StockBalances.Where(b => b.LocationId == site.Bin && b.ItemId == site.Item).Select(b => b.Quantity).SingleAsync());
        Assert.False(await db.StockCounts.AnyAsync(c => c.LocationId == site.Bin));
    }

    [Fact]
    public async Task A_failed_receipt_write_rolls_back_every_adjustment_and_observation_then_recovers()
    {
        var site = await SiteAsync(); await ReceiptAsync(site, 4);
        using var scope = fixture.Services.CreateScope(); var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var input = Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None), [new(site.Sku, 2)]);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION inventory.fail_bin_receipt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test bin receipt failure'; END $$;
            CREATE TRIGGER fail_bin_receipt BEFORE INSERT ON inventory."BinCounts" FOR EACH ROW EXECUTE FUNCTION inventory.fail_bin_receipt();
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => counter.ConfirmAsync(input, CancellationToken.None));
            Assert.Equal(4m, await db.StockBalances.Where(b => b.LocationId == site.Bin && b.ItemId == site.Item).Select(b => b.Quantity).SingleAsync());
            Assert.False(await db.StockCounts.AnyAsync(count => count.LocationId == site.Bin));
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.ItemId == site.Item));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_bin_receipt ON inventory.\"BinCounts\"; DROP FUNCTION inventory.fail_bin_receipt();"); }
        var recovered = await counter.ConfirmAsync(input, CancellationToken.None);
        Assert.True(recovered.IsSuccess); Assert.Equal(-2m, Assert.Single(recovered.Value.Counts).Variance);
    }

    [Fact]
    public async Task Simultaneous_identical_confirmations_record_one_count_and_one_adjustment()
    {
        var site = await SiteAsync(); var input = Instruction(site, 0, [new(site.Sku, 5)]);
        async Task<BinCountOutcome> Confirm() { using var scope = fixture.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IBlindBinCounting>().ConfirmAsync(input, CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null); return result.Value; }
        var results = await Task.WhenAll(Confirm(), Confirm());
        Assert.Equal(results[0].Counts, results[1].Counts);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Single(await db.StockCounts.Where(count => count.LocationId == site.Bin).ToListAsync());
        Assert.Single(await db.StockMovements.Where(movement => movement.ItemId == site.Item).ToListAsync());
    }

    [Fact]
    public async Task Observed_batches_pallets_and_conditions_remain_distinct_and_invalid_metadata_is_atomic()
    {
        var site = await SiteAsync();
        using var scope = fixture.Services.CreateScope(); var masters = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        await masters.Items.Where(item => item.Id == site.Item).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsLotTracked, true));
        var counter = scope.ServiceProvider.GetRequiredService<IBlindBinCounting>();
        var input = Instruction(site, 0, [new(site.Sku, 2, "A", Sscc: "000000000000000017"), new(site.Sku, 3, "B", Condition: StockCondition.Quarantine)]);
        var result = await counter.ConfirmAsync(input, CancellationToken.None); Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(2, result.Value.Counts.Count);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var balances = await db.StockBalances.Where(balance => balance.LocationId == site.Bin).ToListAsync();
        Assert.Equal(2, balances.Count); Assert.All(balances, balance => Assert.Null(balance.FirstReceivedAt));
        Assert.Contains(balances, balance => balance.LicensePlateId is not null && balance.Quantity == 2);
        Assert.Contains(balances, balance => balance.Status == InventoryStatus.Quarantine && balance.Quantity == 3);
        var invalid = await counter.ConfirmAsync(Instruction(site, await counter.ReadVersionAsync(site.Bin, CancellationToken.None),
            [new(site.Sku, 1, "A"), new(site.Sku, 1, "NEW", Sscc: "wrong")]), CancellationToken.None);
        Assert.True(invalid.IsFailure); Assert.False(await db.Lots.AnyAsync(lot => lot.ItemId == site.Item && lot.LotNumber == "NEW"));
        Assert.Equal(5m, await db.StockBalances.Where(balance => balance.LocationId == site.Bin).SumAsync(balance => balance.Quantity));
    }

    private static BinCountInstruction Instruction(Site site, long version, IReadOnlyList<BinCountObservation> observed) =>
        new(Guid.CreateVersion7(), site.Bin, version, observed, Guid.CreateVersion7().ToString(), new PostingActor(Guid.CreateVersion7(), "bin-test"), "Bin audit");

    private async Task<HttpClient> ClientAsync(string role)
    {
        using var scope = fixture.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
        var name = $"BIN{Guid.NewGuid():N}"[..15];
        var user = new CountflowUser { UserName = name, DisplayName = name, OperatorCode = name, Email = name + "@countflow.test", EmailConfirmed = true };
        const string secret = "Countflow#2026";
        var created = await users.CreateAsync(user, secret); Assert.True(created.Succeeded);
        await users.AddToRoleAsync(user, role);
        var client = fixture.CreateClient(); var login = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(name, secret));
        login.EnsureSuccessStatusCode(); var token = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString()); return client;
    }
    private static object Credentials(string identifier, string secret) => new { identifier, password = secret };
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.CreateVersion7().ToString()); return client.SendAsync(request);
    }

    private async Task ReceiptAsync(Site site, decimal quantity)
    {
        using var scope = fixture.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<StockLedger>().PostAsync(new PostingCommand {
            ItemId = site.Item, Quantity = quantity, From = new Holding(site.Supplier, null, InventoryStatus.Available),
            To = new Holding(site.Bin, null, InventoryStatus.Available), Type = MovementType.Receipt, Reason = MovementReason.PurchaseOrderReceipt,
            IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = new LedgerActor(Guid.CreateVersion7(), "bin-test") }, CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    private async Task<Site> SiteAsync()
    {
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var code = $"BIN-{Guid.NewGuid():N}"[..13]; var warehouse = new Warehouse { Code = code, Name = "Blind count test" };
        var area = new Area { WarehouseId = warehouse.Id, Code = "STOR", Name = "Storage" }; var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
        Location Location(string suffix, LocationType type) => new() { WarehouseId = warehouse.Id, ZoneId = zone.Id, Code = code + suffix, Type = type };
        var supplier = Location("-SUP", LocationType.Supplier); var bin = Location("-BIN", LocationType.Bulk); var adjustment = Location("-ADJ", LocationType.Adjustment);
        var item = new Item { Sku = code, Description = "Discovered stock", BaseUom = "EA" };
        db.Warehouses.Add(warehouse); db.Areas.Add(area); db.Zones.Add(zone); db.Locations.AddRange(supplier, bin, adjustment); db.Items.Add(item);
        await db.SaveChangesAsync(); return new(warehouse.Id, bin.Id, supplier.Id, item.Id, item.Sku);
    }

    private sealed record Site(Guid Warehouse, Guid Bin, Guid Supplier, Guid Item, string Sku);
}
