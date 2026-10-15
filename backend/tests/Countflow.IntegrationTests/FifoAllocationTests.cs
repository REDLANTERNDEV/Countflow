using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Countflow.IntegrationTests;

public sealed class FifoAllocationTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public void Catalogue_accepts_fifo_as_an_additive_weighted_preference()
    {
        var set = new RuleSet { Kind = DecisionKind.Allocation, Name = "FIFO", Version = 1 };
        set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = "allocation.fifo", Role = RuleRole.Preference, Order = 1, Weight = 3 });
        var built = AllocationRuleCatalogue.Build(set);
        Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Code : null);
        Assert.Contains("allocation.fifo", AllocationRuleCatalogue.PreferenceCodes);
        Assert.Equal(3m, Assert.Single(built.Value.Preferences).Weight);
    }

    [Fact]
    public async Task Receipt_age_survives_putaway_and_replenishment_and_matches_history()
    {
        var scenario = await ScenarioAsync();
        var receipt = await PostAsync(scenario, scenario.Supplier, scenario.Dock, 20, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Dock, scenario.Bulk, 20, MovementType.Putaway, MovementReason.PutawayComplete);
        await PostAsync(scenario, scenario.Bulk, scenario.Face, 5, MovementType.Replenishment, MovementReason.ReplenishmentPull);
        using var scope = fixture.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var received = await inventory.StockMovements.Where(m => m.Id == receipt.MovementId).Select(m => m.OccurredAt).SingleAsync();
        var holdings = await scope.ServiceProvider.GetRequiredService<IStockAvailability>().HoldingsAsync(scenario.Warehouse, scenario.Item, CancellationToken.None);
        Assert.Equal(2, holdings.Count);
        foreach (var holding in holdings)
            Assert.Equal(received, holding.FirstReceivedAt);
        var snapshots = new List<WarehouseAsOf>();
        await foreach (var snapshot in scope.ServiceProvider.GetRequiredService<IStockHistory>().WalkAsync(
            [scenario.Dock, scenario.Bulk, scenario.Face], [DateTimeOffset.UtcNow], CancellationToken.None)) snapshots.Add(snapshot);
        foreach (var holding in Assert.Single(snapshots).Holdings.Where(holding => holding.Quantity > 0))
            Assert.Equal(received, holding.FirstReceivedAt);
    }

    [Fact]
    public async Task Backfill_restores_existing_history_once_without_changing_quantities_or_ledger()
    {
        var scenario = await ScenarioAsync();
        var receipt = await PostAsync(scenario, scenario.Supplier, scenario.Dock, 8, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Dock, scenario.Bulk, 8, MovementType.Putaway, MovementReason.PutawayComplete);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var received = await db.StockMovements.Where(m => m.Id == receipt.MovementId).Select(m => m.OccurredAt).SingleAsync();
        var before = await db.StockBalances.Where(b => b.ItemId == scenario.Item).ToDictionaryAsync(b => b.Id, b => b.Quantity);
        var ledgerCount = await db.StockMovements.CountAsync();
        await db.StockBalances.Where(b => b.ItemId == scenario.Item).ExecuteUpdateAsync(update => update.SetProperty(b => b.FirstReceivedAt, (DateTimeOffset?)null));
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false));
        var seeder = scope.ServiceProvider.GetServices<IModuleSeeder>().OfType<ReceiptAgeBackfill>().Single();
        await seeder.SeedAsync(CancellationToken.None);
        Assert.Equal(received, await db.StockBalances.Where(b => b.ItemId == scenario.Item && b.LocationId == scenario.Bulk).Select(b => b.FirstReceivedAt).SingleAsync());
        Assert.True(await db.ReceiptAgeProjectionStates.Select(state => state.Completed).SingleAsync());
        await seeder.SeedAsync(CancellationToken.None);
        var after = await db.StockBalances.Where(b => b.ItemId == scenario.Item).ToDictionaryAsync(b => b.Id, b => b.Quantity);
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
        Assert.Equal(ledgerCount, await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Cohort_age_survives_partial_draw_and_merge_but_resets_after_depletion()
    {
        var scenario = await ScenarioAsync();
        var original = await PostAsync(scenario, scenario.Supplier, scenario.Dock, 20, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Dock, scenario.Face, 5, MovementType.Transfer, MovementReason.ConsolidationMove);
        await PostAsync(scenario, scenario.Supplier, scenario.Face, 5, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var first = await db.StockMovements.Where(m => m.Id == original.MovementId).Select(m => m.OccurredAt).SingleAsync();
        Assert.Equal(first, await AgeAsync(db, scenario.Item, scenario.Dock));
        Assert.Equal(first, await AgeAsync(db, scenario.Item, scenario.Face));
        await PostAsync(scenario, scenario.Face, scenario.Bulk, 10, MovementType.Transfer, MovementReason.ConsolidationMove);
        Assert.Null(await AgeAsync(db, scenario.Item, scenario.Face));
        var refill = await PostAsync(scenario, scenario.Supplier, scenario.Face, 3, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        var latest = await db.StockMovements.Where(m => m.Id == refill.MovementId).Select(m => m.OccurredAt).SingleAsync();
        Assert.True(latest > first); Assert.Equal(latest, await AgeAsync(db, scenario.Item, scenario.Face));
    }

    [Fact]
    public async Task Unknown_origin_stays_unknown_when_mixed_or_transferred()
    {
        var scenario = await ScenarioAsync();
        await PostAsync(scenario, scenario.Adjustment, scenario.Face, 2, MovementType.Adjustment, MovementReason.ManualCorrection);
        await PostAsync(scenario, scenario.Supplier, scenario.Face, 3, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Face, scenario.Bulk, 1, MovementType.Transfer, MovementReason.ConsolidationMove);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Null(await AgeAsync(db, scenario.Item, scenario.Face)); Assert.Null(await AgeAsync(db, scenario.Item, scenario.Bulk));
    }

    [Theory]
    [InlineData(MovementType.Adjustment, MovementReason.ManualCorrection)]
    [InlineData(MovementType.CountAdjustment, MovementReason.ManualCorrection)]
    public async Task Discovered_stock_does_not_inherit_age_from_prior_corrections(MovementType type, MovementReason reason)
    {
        var scenario = await ScenarioAsync();
        await PostAsync(scenario, scenario.Supplier, scenario.Face, 4, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Face, scenario.Adjustment, 2, type, reason);
        await PostAsync(scenario, scenario.Adjustment, scenario.Bulk, 1, type, reason);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Null(await AgeAsync(db, scenario.Item, scenario.Bulk));
        await foreach (var snapshot in scope.ServiceProvider.GetRequiredService<IStockHistory>().WalkAsync(
            [scenario.Bulk], [DateTimeOffset.UtcNow], CancellationToken.None))
            Assert.Null(Assert.Single(snapshot.Holdings).FirstReceivedAt);
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false));
        await scope.ServiceProvider.GetServices<IModuleSeeder>().OfType<ReceiptAgeBackfill>().Single().SeedAsync(CancellationToken.None);
        Assert.Null(await AgeAsync(db, scenario.Item, scenario.Bulk));
    }

    [Fact]
    public async Task Live_fifo_and_replay_choose_old_undated_stock_even_after_it_moves()
    {
        var scenario = await ScenarioAsync();
        await PostAsync(scenario, scenario.Supplier, scenario.Bulk, 10, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Supplier, scenario.Face, 10, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RulesDbContext>();
        var active = new RuleSet { Kind = DecisionKind.Allocation, Name = "FIFO active", Version = 1, WarehouseId = scenario.Warehouse, Status = RuleSetStatus.Active };
        var draft = new RuleSet { Kind = DecisionKind.Allocation, Name = "FIFO candidate", Version = 1, WarehouseId = scenario.Warehouse };
        foreach (var set in new[] { active, draft })
        {
            set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = "allocation.stock_available", Role = RuleRole.Constraint, Order = 1 });
            set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = "allocation.fifo", Role = RuleRole.Preference, Weight = 1, Order = 2 });
            db.RuleSets.Add(set);
        }
        await db.SaveChangesAsync(); var from = DateTimeOffset.UtcNow.AddSeconds(-1);
        var allocated = await scope.ServiceProvider.GetRequiredService<IAllocationAdvisor>().AllocateAsync(new AllocationQuery {
            WarehouseId = scenario.Warehouse, ItemId = scenario.Item, Quantity = 2, Actor = new PostingActor(Guid.CreateVersion7(), "fifo-test") }, CancellationToken.None);
        Assert.True(allocated.IsSuccess, allocated.IsFailure ? allocated.Error.Code : null);
        Assert.Equal(scenario.Bulk, Assert.Single(allocated.Value.Lines).LocationId);
        var to = DateTimeOffset.UtcNow;
        await PostAsync(scenario, scenario.Bulk, scenario.Face, 10, MovementType.Transfer, MovementReason.ConsolidationMove);
        var replay = await scope.ServiceProvider.GetRequiredService<IShadowReplay>().RunAsync(new ReplayScope(scenario.Warehouse, DecisionKind.Allocation, from, to), draft.Id, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Code : null);
        Assert.Equal(1, replay.Value.ReplayedCount); Assert.Equal(1, replay.Value.ExactCount); Assert.Equal(0, replay.Value.ChangedCount);
    }

    [Fact]
    public async Task One_batch_propagates_age_from_receipt_to_later_movements()
    {
        var scenario = await ScenarioAsync(); using var scope = fixture.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<StockLedger>().PostBatchAsync([
            Command(scenario, scenario.Supplier, scenario.Dock, 6, MovementType.Receipt, MovementReason.PurchaseOrderReceipt),
            Command(scenario, scenario.Dock, scenario.Bulk, 6, MovementType.Putaway, MovementReason.PutawayComplete)], CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>(); var receiptId = result.Value[0].MovementId;
        var received = await db.StockMovements.Where(m => m.Id == receiptId).Select(m => m.OccurredAt).SingleAsync();
        Assert.Equal(received, await AgeAsync(db, scenario.Item, scenario.Bulk)); Assert.Null(await AgeAsync(db, scenario.Item, scenario.Dock));
    }

    [Fact]
    public async Task Concurrent_receipts_and_duplicate_retries_keep_one_oldest_age()
    {
        var scenario = await ScenarioAsync();
        await Task.WhenAll(PostAsync(scenario, scenario.Supplier, scenario.Face, 4, MovementType.Receipt, MovementReason.PurchaseOrderReceipt),
            PostAsync(scenario, scenario.Supplier, scenario.Face, 5, MovementType.Receipt, MovementReason.PurchaseOrderReceipt));
        var retry = Command(scenario, scenario.Supplier, scenario.Face, 1, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        async Task Repeat() { using var scope = fixture.Services.CreateScope(); var result = await scope.ServiceProvider.GetRequiredService<StockLedger>().PostAsync(retry, CancellationToken.None); Assert.True(result.IsSuccess); }
        await Task.WhenAll(Repeat(), Repeat());
        using var observed = fixture.Services.CreateScope(); var db = observed.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var first = await db.StockMovements.Where(m => m.ItemId == scenario.Item).MinAsync(m => m.OccurredAt);
        Assert.Equal(first, await AgeAsync(db, scenario.Item, scenario.Face));
        Assert.Equal(10m, await db.StockBalances.Where(b => b.ItemId == scenario.Item && b.LocationId == scenario.Face).Select(b => b.Quantity).SingleAsync());
        Assert.Equal(3, await db.StockMovements.CountAsync(m => m.ItemId == scenario.Item));
    }

    [Fact]
    public async Task Backfill_failure_rolls_back_age_and_completion_marker_then_recovers()
    {
        var scenario = await ScenarioAsync(); await PostAsync(scenario, scenario.Supplier, scenario.Bulk, 4, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await db.StockBalances.Where(b => b.ItemId == scenario.Item).ExecuteUpdateAsync(update => update.SetProperty(b => b.FirstReceivedAt, (DateTimeOffset?)null));
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false));
        var sql = $"""
            CREATE FUNCTION inventory."RejectFifoBackfill"() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."ItemId" = '{scenario.Item:D}'::uuid THEN RAISE EXCEPTION 'deliberate backfill failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER "RejectFifoBackfill" BEFORE UPDATE OF "FirstReceivedAt" ON inventory."StockBalances"
            FOR EACH ROW EXECUTE FUNCTION inventory."RejectFifoBackfill"();
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
        var seeder = scope.ServiceProvider.GetServices<IModuleSeeder>().OfType<ReceiptAgeBackfill>().Single();
        try
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => seeder.SeedAsync(CancellationToken.None));
            Assert.Null(await AgeAsync(db, scenario.Item, scenario.Bulk)); Assert.False(await db.ReceiptAgeProjectionStates.Select(state => state.Completed).SingleAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""DROP TRIGGER "RejectFifoBackfill" ON inventory."StockBalances"; DROP FUNCTION inventory."RejectFifoBackfill"();""");
        }
        await seeder.SeedAsync(CancellationToken.None); Assert.NotNull(await AgeAsync(db, scenario.Item, scenario.Bulk));
    }

    private static Task<DateTimeOffset?> AgeAsync(InventoryDbContext db, Guid item, Guid location) =>
        db.StockBalances.Where(balance => balance.ItemId == item && balance.LocationId == location).Select(balance => balance.FirstReceivedAt).SingleAsync();

    [Fact]
    public async Task Fifo_scores_are_bounded_unknown_is_neutral_and_constraints_still_win()
    {
        var scenario = await ScenarioAsync();
        await PostAsync(scenario, scenario.Supplier, scenario.Bulk, 2, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        await PostAsync(scenario, scenario.Supplier, scenario.Face, 2, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope();
        var holdings = await scope.ServiceProvider.GetRequiredService<IStockAvailability>().HoldingsAsync(scenario.Warehouse, scenario.Item, CancellationToken.None);
        var old = holdings.Single(holding => holding.LocationId == scenario.Bulk); var young = holdings.Single(holding => holding.LocationId == scenario.Face);
        var item = await scope.ServiceProvider.GetRequiredService<IItemDirectory>().FindAsync(scenario.Item, CancellationToken.None);
        var locations = await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindStorageLocationsAsync([scenario.Bulk, scenario.Face], CancellationToken.None);
        var context = new AllocationContext(item!, 1, null, DateOnly.FromDateTime(DateTime.UtcNow), locations, null, null, 0, 0)
            { EarliestReceipt = old.FirstReceivedAt, LatestReceipt = young.FirstReceivedAt };
        var set = new RuleSet { Kind = DecisionKind.Allocation, Name = "FIFO scored", Version = 1 };
        set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = "allocation.stock_available", Role = RuleRole.Constraint, Order = 1 });
        set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = "allocation.fifo", Role = RuleRole.Preference, Weight = 1, Order = 2 });
        var built = AllocationRuleCatalogue.Build(set).Value; var preference = Assert.Single(built.Preferences);
        Assert.Equal(1m, preference.Score(old, context).Value); Assert.Equal(0m, preference.Score(young, context).Value);
        var unknown = young with { Id = Guid.CreateVersion7(), FirstReceivedAt = null, LocationCode = "Unknown" };
        Assert.Equal(0.5m, preference.Score(unknown, context).Value); Assert.Equal("rules.allocation.receipt_age_unknown", preference.Score(unknown, context).Reason.Code);
        var blocked = old with { ReservedQuantity = old.OnHandQuantity };
        var decision = RuleEngine.Evaluate(DecisionKind.Allocation, built.Stamp, [blocked, young], context, built.Constraints, built.Preferences);
        Assert.Equal(young.Id, decision.Winner!.Candidate.Id); Assert.False(decision.Candidates.Single(candidate => candidate.Candidate.Id == old.Id).IsAdmissible);
        var tied = young with { FirstReceivedAt = old.FirstReceivedAt };
        var same = context with { LatestReceipt = old.FirstReceivedAt };
        var forward = RuleEngine.Evaluate(DecisionKind.Allocation, built.Stamp, [old, tied], same, built.Constraints, built.Preferences);
        var reversed = RuleEngine.Evaluate(DecisionKind.Allocation, built.Stamp, [tied, old], same, built.Constraints, built.Preferences);
        Assert.Equal(forward.Winner!.Candidate.Id, reversed.Winner!.Candidate.Id);
    }

    [Fact]
    public async Task Status_changes_preserve_receipt_age()
    {
        var scenario = await ScenarioAsync(); var receipt = await PostAsync(scenario, scenario.Supplier, scenario.Face, 4, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope(); var ledger = scope.ServiceProvider.GetRequiredService<StockLedger>();
        var hold = Command(scenario, scenario.Face, scenario.Face, 2, MovementType.StatusChange, MovementReason.QualityHoldApplied)
            with { To = new Holding(scenario.Face, null, InventoryStatus.Quarantine) };
        Assert.True((await ledger.PostAsync(hold, CancellationToken.None)).IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>(); var received = await db.StockMovements.Where(m => m.Id == receipt.MovementId).Select(m => m.OccurredAt).SingleAsync();
        var ages = await db.StockBalances.Where(balance => balance.ItemId == scenario.Item && balance.LocationId == scenario.Face).Select(balance => balance.FirstReceivedAt).ToListAsync();
        Assert.Equal(2, ages.Count); Assert.All(ages, age => Assert.Equal(received, age));
    }

    [Fact]
    public async Task Concurrent_backfills_complete_once_with_the_same_age()
    {
        var scenario = await ScenarioAsync(); var receipt = await PostAsync(scenario, scenario.Supplier, scenario.Bulk, 2, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var observed = fixture.Services.CreateScope(); var db = observed.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await db.StockBalances.Where(balance => balance.ItemId == scenario.Item).ExecuteUpdateAsync(update => update.SetProperty(balance => balance.FirstReceivedAt, (DateTimeOffset?)null));
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false));
        async Task Rebuild() { using var scope = fixture.Services.CreateScope(); await scope.ServiceProvider.GetServices<IModuleSeeder>().OfType<ReceiptAgeBackfill>().Single().SeedAsync(CancellationToken.None); }
        await Task.WhenAll(Rebuild(), Rebuild());
        var received = await db.StockMovements.Where(m => m.Id == receipt.MovementId).Select(m => m.OccurredAt).SingleAsync();
        Assert.Equal(received, await AgeAsync(db, scenario.Item, scenario.Bulk)); Assert.True(await db.ReceiptAgeProjectionStates.Select(state => state.Completed).SingleAsync());
    }

    [Fact]
    public async Task Backfill_never_assigns_a_known_age_to_a_drifted_quantity()
    {
        var scenario = await ScenarioAsync(); await PostAsync(scenario, scenario.Supplier, scenario.Bulk, 4, MovementType.Receipt, MovementReason.PurchaseOrderReceipt);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await db.StockBalances.Where(balance => balance.ItemId == scenario.Item && balance.LocationId == scenario.Bulk).ExecuteUpdateAsync(update => update.SetProperty(balance => balance.Quantity, 5));
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false));
        var seeder = scope.ServiceProvider.GetServices<IModuleSeeder>().OfType<ReceiptAgeBackfill>().Single(); await seeder.SeedAsync(CancellationToken.None);
        Assert.Null(await AgeAsync(db, scenario.Item, scenario.Bulk));
        Assert.Equal(5m, await db.StockBalances.Where(balance => balance.ItemId == scenario.Item && balance.LocationId == scenario.Bulk).Select(balance => balance.Quantity).SingleAsync());
        await db.StockBalances.Where(balance => balance.ItemId == scenario.Item && balance.LocationId == scenario.Bulk).ExecuteUpdateAsync(update => update.SetProperty(balance => balance.Quantity, 4));
        await db.ReceiptAgeProjectionStates.ExecuteUpdateAsync(update => update.SetProperty(state => state.Completed, false)); await seeder.SeedAsync(CancellationToken.None);
    }

    private async Task<Scenario> ScenarioAsync()
    {
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var code = $"FIFO-{Guid.NewGuid():N}"[..13];
        var warehouse = new Warehouse { Code = code, Name = "FIFO test" };
        var area = new Area { WarehouseId = warehouse.Id, Code = "STOR", Name = "Storage" };
        var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
        Location Location(string suffix, LocationType type) => new() { WarehouseId = warehouse.Id, ZoneId = zone.Id, Code = code + suffix, Type = type };
        var supplier = Location("-SUP", LocationType.Supplier); var dock = Location("-DOCK", LocationType.ReceivingDock);
        var bulk = Location("-BULK", LocationType.Bulk); var face = Location("-FACE", LocationType.PickFace);
        var adjustment = Location("-ADJ", LocationType.Adjustment);
        var item = new Item { Sku = code, Description = "Undated FIFO item", BaseUom = "EA" };
        db.Warehouses.Add(warehouse); db.Areas.Add(area); db.Zones.Add(zone); db.Locations.AddRange(supplier, dock, bulk, face, adjustment); db.Items.Add(item);
        await db.SaveChangesAsync(); return new Scenario(warehouse.Id, item.Id, supplier.Id, dock.Id, bulk.Id, face.Id, adjustment.Id);
    }

    private async Task<MovementReceipt> PostAsync(Scenario scenario, Guid from, Guid to, decimal quantity, MovementType type, MovementReason reason)
    {
        using var scope = fixture.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<StockLedger>().PostAsync(Command(scenario, from, to, quantity, type, reason), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null); return result.Value;
    }

    private static PostingCommand Command(Scenario scenario, Guid from, Guid to, decimal quantity, MovementType type, MovementReason reason) => new() {
        ItemId = scenario.Item, Quantity = quantity, From = new Holding(from, null, InventoryStatus.Available), To = new Holding(to, null, InventoryStatus.Available),
        Type = type, Reason = reason, IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = new LedgerActor(Guid.CreateVersion7(), "fifo-test") };

    private sealed record Scenario(Guid Warehouse, Guid Item, Guid Supplier, Guid Dock, Guid Bulk, Guid Face, Guid Adjustment);
}
