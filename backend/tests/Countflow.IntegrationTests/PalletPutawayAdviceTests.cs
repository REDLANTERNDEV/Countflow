using System.Data.Common;
using Countflow.Modules.Inventory.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using System.Text.Json;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class PalletPutawayAdviceTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    private static readonly PostingActor Actor = new(Guid.NewGuid(), "pallet-advice-test");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Whole_pallet_advice_uses_both_skus_and_persists_the_exact_contents()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var contents = await ReceivePalletAsync(services, site);
        var advisor = services.GetRequiredService<IPalletPutawayAdvisor>();
        var mutableInput = contents.ToArray();
        var pending = advisor.SuggestAsync(Query(site, mutableInput), CancellationToken.None);
        mutableInput[1] = mutableInput[1] with { Quantity = 100m };
        var advice = await pending;
        Assert.True(advice.IsSuccess, advice.IsFailure ? advice.Error.ToString() : "");
        Assert.Equal(site.Wide.Id, advice.Value.LocationId);

        var db = services.GetRequiredService<RulesDbContext>();
        var trace = await db.DecisionTraces.AsNoTracking().SingleAsync(t => t.Id == advice.Value.DecisionTraceId);
        var inputs = JsonSerializer.Deserialize<PutawayInputs>(trace.InputsJson, Json)!;
        Assert.NotNull(inputs.PalletContents);
        Assert.Equivalent(contents, inputs.PalletContents);
        Assert.Equal(site.PlateId, inputs.LicensePlateId);
        Assert.False(trace.IsPreview);
        Assert.Equal(Actor.UserId, trace.UserId);
        Assert.Equal(advice.Value.LocationId, trace.SelectedCandidateId);
    }

    [Fact]
    public async Task Replay_preserves_both_intents_after_the_plate_and_destination_stock_change()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var contents = await ReceivePalletAsync(services, site);
        var posting = services.GetRequiredService<IStockPosting>();
        // Arrange occupied storage through the ledger, not a balance-table write.
        var existing = await posting.ReceiveAsync(new ReceiptInstruction
        {
            ItemId = site.Second.Id, Quantity = 4m, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(existing.IsSuccess);
        var single = await services.GetRequiredService<IPutawayAdvisor>().SuggestAsync(new PutawayQuery
        {
            WarehouseId = site.Warehouse.Id, FromLocationId = site.Dock.Id,
            ItemId = site.First.Id, Quantity = 6m, LicensePlateId = site.PlateId, Actor = Actor
        }, CancellationToken.None);
        var whole = await services.GetRequiredService<IPalletPutawayAdvisor>().SuggestAsync(Query(site, contents), CancellationToken.None);
        Assert.True(single.IsSuccess);
        Assert.True(whole.IsSuccess);
        Assert.Equal(site.Small.Id, single.Value.LocationId);
        Assert.Equal(site.Wide.Id, whole.Value.LocationId);
        // Twelve incoming cubic metres / sixteen free at decision time.
        Assert.Equal(0.75m, whole.Value.Reasons.Single(reason => reason.RuleCode == "putaway.cube_utilisation").Value);

        // Later receipts must not redefine the question stored on either trace.
        var later = await services.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
        {
            ItemId = site.First.Id, Quantity = 100m, ToLocationId = site.Dock.Id,
            Sscc = site.Sscc, IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(later.IsSuccess);
        var destinationChanged = await posting.ReceiveAsync(new ReceiptInstruction
        {
            ItemId = site.Second.Id, Quantity = 4m, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(destinationChanged.IsSuccess);

        var db = services.GetRequiredService<RulesDbContext>();
        var activeId = await db.RuleSets.Where(set => set.Kind == DecisionKind.Putaway && set.WarehouseId == null
            && set.Status == Countflow.Modules.Rules.Domain.RuleSetStatus.Active).Select(set => set.Id).SingleAsync();
        var replay = await services.GetRequiredService<IShadowReplay>().RunAsync(new ReplayScope(
            site.Warehouse.Id, DecisionKind.Putaway, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1)),
            activeId, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.ToString() : "");
        Assert.Equal(2, replay.Value.ReplayedCount);
        Assert.Equal(2, replay.Value.ExactCount);
        Assert.Equal(0, replay.Value.UnreplayableCount);
        var oldDecision = replay.Value.Decisions.Single(d => d.TraceId == single.Value.DecisionTraceId);
        var newDecision = replay.Value.Decisions.Single(d => d.TraceId == whole.Value.DecisionTraceId);
        Assert.Equal(site.Small.Code, oldDecision.BaselineKey);
        Assert.Equal(site.Wide.Code, newDecision.BaselineKey);
        Assert.Equal(single.Value.Score, oldDecision.BaselineScore);
        Assert.Equal(whole.Value.Score, newDecision.BaselineScore);
        Assert.Equal(ReplayOutcome.Unchanged, newDecision.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_contents_do_not_create_a_trace(int quantity)
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var db = services.GetRequiredService<RulesDbContext>();
        var before = await db.DecisionTraces.CountAsync(t => t.UserId == Actor.UserId);
        var advisor = services.GetRequiredService<IPalletPutawayAdvisor>();
        var invalid = await advisor.SuggestAsync(Query(site,
            [new PlateLine(site.First.Id, null, StockCondition.Available, quantity)]), CancellationToken.None);
        var empty = await advisor.SuggestAsync(Query(site, []), CancellationToken.None);
        Assert.True(invalid.IsFailure);
        Assert.True(empty.IsFailure);
        Assert.Equal(before, await db.DecisionTraces.CountAsync(t => t.UserId == Actor.UserId));
    }

    [Fact]
    public async Task Conflicting_temperatures_still_produce_a_persisted_no_location_explanation()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        site.Second.TemperatureClass = TemperatureClass.Chilled;
        await services.GetRequiredService<MasterDataDbContext>().SaveChangesAsync();
        var contents = await ReceivePalletAsync(services, site);
        var advice = await services.GetRequiredService<IPalletPutawayAdvisor>().SuggestAsync(Query(site, contents), CancellationToken.None);
        Assert.True(advice.IsSuccess);
        Assert.Null(advice.Value.LocationId);
        Assert.Equal(0, advice.Value.AdmissibleCount);
        var trace = await services.GetRequiredService<IDecisionTraceReader>().FindAsync(advice.Value.DecisionTraceId, CancellationToken.None);
        Assert.NotNull(trace);
        Assert.Null(trace.SelectedId);
        Assert.NotEmpty(trace.Candidates);
        Assert.All(trace.Candidates, candidate => Assert.False(candidate.IsAdmissible));
    }

    [Fact]
    public async Task A_missing_item_refuses_the_whole_decision_without_writing_a_partial_trace()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var db = services.GetRequiredService<RulesDbContext>();
        var before = await db.DecisionTraces.CountAsync(t => t.UserId == Actor.UserId);
        var advice = await services.GetRequiredService<IPalletPutawayAdvisor>().SuggestAsync(Query(site,
            [new PlateLine(site.First.Id, null, StockCondition.Available, 6m),
             new PlateLine(Guid.NewGuid(), null, StockCondition.Available, 6m)]), CancellationToken.None);
        Assert.True(advice.IsFailure);
        Assert.Equal("rules.item_not_found", advice.Error.Code);
        Assert.Equal(before, await db.DecisionTraces.CountAsync(t => t.UserId == Actor.UserId));
    }

    [Fact]
    public async Task Batch_occupancy_keeps_item_lot_perspectives_and_counts_quarantined_stock()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var contents = await ReceivePalletAsync(services, site, quarantineSecond: true);
        var advice = await services.GetRequiredService<IPalletPutawayAdvisor>().SuggestAsync(Query(site, contents), CancellationToken.None);
        Assert.True(advice.IsSuccess);
        var moved = await services.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            TaskId = Guid.NewGuid(), DecisionTraceId = advice.Value.DecisionTraceId,
            IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(moved.IsSuccess, moved.IsFailure ? moved.Error.ToString() : "");
        var first = new StockSubject(site.First.Id, null);
        var second = new StockSubject(site.Second.Id, null);
        var otherLot = new StockSubject(site.First.Id, Guid.NewGuid());
        var stock = services.GetRequiredService<IStockQuery>();
        var batch = await stock.OccupancyAsync([site.Wide.Id], [first, second, otherLot, first], CancellationToken.None);
        Assert.Equal(3, batch.Count);
        foreach (var subject in batch.Values)
        {
            var occupied = Assert.Single(subject).Value;
            Assert.Equal(2, occupied.DistinctItems);
            Assert.Equal(12m, occupied.TotalUnits);
            Assert.Equal(12m, occupied.OccupiedVolumeM3);
            Assert.Equal(12m, occupied.OccupiedWeightKg);
            Assert.Equal(6m, occupied.SubjectItemUnits);
        }
        Assert.True(batch[first][site.Wide.Id].HoldsSubjectLot);
        Assert.True(batch[second][site.Wide.Id].HoldsSubjectLot);
        Assert.False(batch[otherLot][site.Wide.Id].HoldsSubjectLot);
        Assert.True(batch[otherLot][site.Wide.Id].HoldsOtherLotOfSubjectItem);
        var empty = await stock.OccupancyAsync([], [first, second], CancellationToken.None);
        Assert.Equal(2, empty.Count);
        Assert.All(empty.Values, value => Assert.Empty(value));
    }

    [Fact]
    public void A_trace_from_before_pallet_contents_were_added_still_parses_as_single_item()
    {
        const string oldJson = """
            {"warehouseId":"00000000-0000-0000-0000-000000000001",
             "itemId":"00000000-0000-0000-0000-000000000002","sku":"OLD",
             "lotId":null,"quantity":6,"condition":"Available",
             "fromLocationId":"00000000-0000-0000-0000-000000000003",
             "licensePlateId":"00000000-0000-0000-0000-000000000004"}
            """;
        var parsed = JsonSerializer.Deserialize<PutawayInputs>(oldJson, Json)!;
        Assert.Null(parsed.PalletContents);
        Assert.Equal(6m, parsed.Quantity);
        Assert.NotNull(parsed.LicensePlateId);
    }

    [Theory]
    [InlineData(StockCondition.Available)]
    [InlineData(StockCondition.Quarantine)]
    public async Task Expected_pallet_contents_refuse_quantity_changes_and_new_condition_rows(StockCondition condition)
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var expected = await ReceivePalletAsync(services, site);
        var posting = services.GetRequiredService<IStockPosting>();
        var added = await posting.ReceiveAsync(new ReceiptInstruction
        {
            ItemId = site.First.Id, Quantity = 1m, ToLocationId = site.Dock.Id,
            Sscc = site.Sscc, Condition = condition, IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(added.IsSuccess);
        var before = await services.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Dock.Id, CancellationToken.None);
        var instruction = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor,
            TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), ExpectedContents = expected
        };
        var result = await posting.PutawayLicensePlateAsync(instruction, CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal("inventory.pallet_contents_changed", result.Error.Code);
        Assert.Equal(before, await services.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Dock.Id, CancellationToken.None));
        Assert.Empty(await services.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Wide.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Whole_pallet_read_holds_out_a_receipt_that_would_create_a_new_balance_row()
    {
        using var setup = fixture.Services.CreateScope();
        var site = await NewSiteAsync(setup.ServiceProvider);
        await ReceivePalletAsync(setup.ServiceProvider, site);
        var pause = new PausePalletRead(site.PlateId);
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<InventoryDbContext>(options => options.AddInterceptors(pause))));
        using var moveScope = app.Services.CreateScope();
        var move = moveScope.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(),
            DecisionTraceId = Guid.NewGuid(), Actor = Actor
        }, CancellationToken.None);
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var receiptScope = fixture.Services.CreateScope();
            var db = receiptScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.OpenConnectionAsync();
            var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            var receipt = receiptScope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
            {
                ItemId = site.First.Id, Quantity = 1m, ToLocationId = site.Dock.Id, Sscc = site.Sscc,
                Condition = StockCondition.Quarantine, IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
            }, CancellationToken.None);
            await using var observer = new NpgsqlConnection(db.Database.GetConnectionString());
            await observer.OpenAsync();
            var blocked = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!receipt.IsCompleted && DateTime.UtcNow < deadline)
            {
                await using var check = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", observer);
                check.Parameters.AddWithValue("pid", pid);
                blocked = (bool)(await check.ExecuteScalarAsync())!;
                if (blocked) break;
                await Task.Delay(10);
            }
            pause.Resume.TrySetResult(true);
            Assert.True((await move).IsSuccess);
            Assert.True((await receipt).IsSuccess);
            Assert.True(blocked, "The new balance writer must wait for the whole-pallet snapshot/posting transaction.");
            var stock = setup.ServiceProvider.GetRequiredService<IStockQuery>();
            Assert.Equal(2, (await stock.PlateContentsAsync(site.PlateId, site.Wide.Id, CancellationToken.None)).Count);
            var after = Assert.Single(await stock.PlateContentsAsync(site.PlateId, site.Dock.Id, CancellationToken.None));
            Assert.Equal(StockCondition.Quarantine, after.Condition);
            Assert.Equal(1m, after.Quantity);
        }
        finally { pause.Resume.TrySetResult(true); await move; }
    }

    [Fact]
    public async Task Expected_pallet_contents_replay_after_moving_and_refuse_a_changed_retry()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var contents = await ReceivePalletAsync(services, site, quarantineSecond: true);
        var instruction = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(),
            DecisionTraceId = Guid.NewGuid(), Actor = Actor, ExpectedContents = contents.Reverse().ToArray()
        };
        var posting = services.GetRequiredService<IStockPosting>();
        var first = await posting.PutawayLicensePlateAsync(instruction, CancellationToken.None);
        Assert.True(first.IsSuccess);
        var replay = await posting.PutawayLicensePlateAsync(instruction, CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.All(replay.Value, movement => Assert.True(movement.WasAlreadyPosted));
        Assert.Equal(first.Value.Select(m => m.MovementId).Order(), replay.Value.Select(m => m.MovementId).Order());
        var normalized = await posting.PutawayLicensePlateAsync(instruction with
        {
            Actor = Actor with { UserName = "renamed operator" },
            ExpectedContents = contents.Select(line => line with { Quantity = 6m }).ToArray()
        }, CancellationToken.None);
        Assert.True(normalized.IsSuccess);
        Assert.All(normalized.Value, movement => Assert.True(movement.WasAlreadyPosted));
        foreach (var changed in new[]
        {
            instruction with { ToLocationId = site.Small.Id },
            instruction with { DecisionTraceId = Guid.NewGuid() },
            instruction with { ExpectedContents = [contents[0]] },
            instruction with { Actor = Actor with { UserId = Guid.NewGuid() } },
            instruction with { TaskId = Guid.NewGuid() },
            instruction with { Notes = "Changed command" }
        })
        {
            var refused = await posting.PutawayLicensePlateAsync(changed, CancellationToken.None);
            Assert.True(refused.IsFailure);
            Assert.Equal("inventory.idempotency_key_reused", refused.Error.Code);
        }
    }

    [Fact]
    public async Task Whole_pallet_waits_for_a_blind_count_before_taking_its_plate_lock()
    {
        using var setup = fixture.Services.CreateScope();
        var site = await NewSiteAsync(setup.ServiceProvider);
        await ReceivePalletAsync(setup.ServiceProvider, site);
        var setupDb = setup.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var count = new NpgsqlConnection(setupDb.Database.GetConnectionString());
        await count.OpenAsync();
        await using var countTransaction = await count.BeginTransactionAsync();
        await using (var table = new NpgsqlCommand("""LOCK TABLE inventory."StockBalances" IN SHARE ROW EXCLUSIVE MODE""", count, countTransaction))
            await table.ExecuteNonQueryAsync();
        using var moving = fixture.Services.CreateScope();
        var db = moving.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await db.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var move = moving.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), Actor = Actor
        }, CancellationToken.None);
        try
        {
            var blocked = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!move.IsCompleted && DateTime.UtcNow < deadline)
            {
                await using var check = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", count, countTransaction);
                check.Parameters.AddWithValue("pid", pid);
                blocked = (bool)(await check.ExecuteScalarAsync())!;
                if (blocked) break;
                await Task.Delay(10);
            }
            Assert.True(blocked);
            // A count holding the stronger table lock must still be able to
            // acquire this plate, without waiting for the writer it holds out.
            await using var plate = new NpgsqlCommand("""SELECT "Id" FROM inventory."LicensePlates" WHERE "Id" = @id FOR NO KEY UPDATE NOWAIT""", count, countTransaction);
            plate.Parameters.AddWithValue("id", site.PlateId);
            Assert.Equal(site.PlateId, (Guid)(await plate.ExecuteScalarAsync())!);
        }
        finally { await countTransaction.RollbackAsync(); }
        Assert.True((await move).IsSuccess);
    }

    [Fact]
    public async Task A_refused_pallet_command_cannot_become_a_success_after_the_contents_change_back()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var expected = await ReceivePalletAsync(services, site);
        var posting = services.GetRequiredService<IStockPosting>();
        var extra = await posting.ReceiveAsync(new ReceiptInstruction
        {
            ItemId = site.First.Id, Quantity = 1m, ToLocationId = site.Dock.Id, Sscc = site.Sscc,
            IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(extra.IsSuccess);
        var command = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(),
            DecisionTraceId = Guid.NewGuid(), Actor = Actor, ExpectedContents = expected
        };
        var refused = await posting.PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.True(refused.IsFailure);
        Assert.Equal("inventory.pallet_contents_changed", refused.Error.Code);
        var restore = await posting.PutawayAsync(new PutawayInstruction
        {
            ItemId = site.First.Id, Quantity = 1m, FromLocationId = site.Dock.Id, ToLocationId = site.Small.Id,
            LicensePlateId = site.PlateId, IdempotencyKey = Guid.NewGuid().ToString(),
            TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), Actor = Actor
        }, CancellationToken.None);
        Assert.True(restore.IsSuccess);
        var lateRetry = await posting.PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.True(lateRetry.IsFailure);
        Assert.Equal(refused.Error, lateRetry.Error);
        Assert.Empty(await services.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Wide.Id, CancellationToken.None));
        var changed = await posting.PutawayLicensePlateAsync(command with { DecisionTraceId = Guid.NewGuid() }, CancellationToken.None);
        Assert.True(changed.IsFailure);
        Assert.Equal("inventory.idempotency_key_reused", changed.Error.Code);
        var fresh = await posting.PutawayLicensePlateAsync(command with { IdempotencyKey = Guid.NewGuid().ToString() }, CancellationToken.None);
        Assert.True(fresh.IsSuccess);
        Assert.Equal(12m, fresh.Value.Sum(movement => movement.Quantity));
    }

    [Fact]
    public async Task A_conflicting_legacy_retry_does_not_poison_the_original_movement_family()
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var expected = await ReceivePalletAsync(services, site);
        var command = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(),
            DecisionTraceId = Guid.NewGuid(), Actor = Actor, ExpectedContents = expected
        };
        var posting = services.GetRequiredService<IStockPosting>();
        var first = await posting.PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess);
        var db = services.GetRequiredService<InventoryDbContext>();
        // Simulate movements written before the command journal was deployed.
        await db.PlatePutawayCommands.Where(row => row.IdempotencyKey == command.IdempotencyKey).ExecuteDeleteAsync();
        foreach (var changed in new[]
        {
            command with { ToLocationId = site.Small.Id },
            command with { Actor = Actor with { UserId = Guid.NewGuid() } },
            command with { Notes = "Different legacy command" }
        })
        {
            var conflict = await posting.PutawayLicensePlateAsync(changed, CancellationToken.None);
            Assert.True(conflict.IsFailure);
            Assert.Equal("inventory.idempotency_key_reused", conflict.Error.Code);
            Assert.False(await db.PlatePutawayCommands.AnyAsync(row => row.IdempotencyKey == command.IdempotencyKey));
        }
        var replay = await posting.PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.All(replay.Value, movement => Assert.True(movement.WasAlreadyPosted));
        Assert.Equal(first.Value.Select(m => m.MovementId).Order(), replay.Value.Select(m => m.MovementId).Order());
        Assert.NotNull((await db.PlatePutawayCommands.AsNoTracking().SingleAsync(row => row.IdempotencyKey == command.IdempotencyKey)).OutcomeJson);
    }

    [Fact]
    public async Task Competing_bodies_on_different_plates_share_one_root_key_result()
    {
        using var setup = fixture.Services.CreateScope();
        var one = await NewSiteAsync(setup.ServiceProvider);
        var two = await NewSiteAsync(setup.ServiceProvider);
        var contentsOne = await ReceivePalletAsync(setup.ServiceProvider, one);
        var contentsTwo = await ReceivePalletAsync(setup.ServiceProvider, two);
        var key = Guid.NewGuid().ToString();
        PlatePutawayInstruction Command(Site site, IReadOnlyList<PlateLine> contents) => new()
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = key, TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), Actor = Actor, ExpectedContents = contents
        };
        var commands = new[] { Command(one, contentsOne), Command(two, contentsTwo) };
        async Task<Countflow.SharedKernel.Results.Result<IReadOnlyList<PostedMovement>>> Run(PlatePutawayInstruction command)
        {
            using var scope = fixture.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(command, CancellationToken.None);
        }
        var results = await Task.WhenAll(commands.Select(Run));
        var winner = Array.FindIndex(results, result => result.IsSuccess);
        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.Equal("inventory.idempotency_key_reused", Assert.Single(results, result => result.IsFailure).Error.Code);
        var db = setup.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var record = await db.PlatePutawayCommands.AsNoTracking().SingleAsync(row => row.IdempotencyKey == key);
        Assert.Equal(commands[winner].LicensePlateId, record.LicensePlateId);
        var stock = setup.ServiceProvider.GetRequiredService<IStockQuery>();
        var sites = new[] { one, two };
        Assert.Empty(await stock.PlateContentsAsync(sites[winner].PlateId, sites[winner].Dock.Id, CancellationToken.None));
        Assert.Empty(await stock.PlateContentsAsync(sites[1 - winner].PlateId, sites[1 - winner].Wide.Id, CancellationToken.None));
        Assert.Equal(12m, (await stock.PlateContentsAsync(sites[1 - winner].PlateId, sites[1 - winner].Dock.Id, CancellationToken.None)).Sum(line => line.Quantity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_inventory_commit_acknowledgement_replays_its_terminal_outcome(bool success)
    {
        using var setup = fixture.Services.CreateScope();
        var site = await NewSiteAsync(setup.ServiceProvider);
        var expected = await ReceivePalletAsync(setup.ServiceProvider, site);
        var lost = new LoseCommandCommitAcknowledgement();
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<InventoryDbContext>(options => options.AddInterceptors(lost))));
        var command = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), Actor = Actor,
            ExpectedContents = success ? expected : expected.Select(line => line with { Quantity = 5m }).ToArray()
        };
        using (var scope = app.Services.CreateScope())
            await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<IStockPosting>()
                .PutawayLicensePlateAsync(command, CancellationToken.None));
        var replay = await setup.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.Equal(success, replay.IsSuccess);
        var db = setup.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.NotNull((await db.PlatePutawayCommands.AsNoTracking().SingleAsync(row => row.IdempotencyKey == command.IdempotencyKey)).OutcomeJson);
        if (success)
        {
            Assert.Equal(2, replay.Value.Count);
            Assert.All(replay.Value, movement => Assert.True(movement.WasAlreadyPosted));
        }
        else Assert.Equal("inventory.pallet_contents_changed", replay.Error.Code);
        Assert.Equal(success ? 2 : 0, await db.StockMovements.CountAsync(movement => movement.TaskId == command.TaskId));
    }

    [Theory]
    [InlineData(124, 1000, true)]
    [InlineData(125, 0, false)]
    [InlineData(36, 1001, false)]
    public async Task Pallet_command_validation_keeps_derived_movement_keys_and_notes_within_storage_limits(int keyLength, int noteLength, bool valid)
    {
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var site = await NewSiteAsync(services);
        var expected = await ReceivePalletAsync(services, site);
        var key = Guid.NewGuid().ToString().PadRight(keyLength, 'x');
        var command = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = key, TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(), Actor = Actor,
            ExpectedContents = expected, Notes = new string('x', noteLength)
        };
        var result = await services.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.Equal(valid, result.IsSuccess);
        if (!valid) Assert.Equal("inventory.pallet_command_invalid", result.Error.Code);
        var db = services.GetRequiredService<InventoryDbContext>();
        Assert.Equal(valid, await db.PlatePutawayCommands.AnyAsync(row => row.IdempotencyKey == key));
        Assert.Equal(valid ? 2 : 0, await db.StockMovements.CountAsync(row => row.TaskId == command.TaskId));
    }

    [Fact]
    public async Task Failure_after_stock_insert_before_outcome_settlement_rolls_back_the_entire_command()
    {
        using var setup = fixture.Services.CreateScope();
        var site = await NewSiteAsync(setup.ServiceProvider);
        var expected = await ReceivePalletAsync(setup.ServiceProvider, site);
        var command = new PlatePutawayInstruction
        {
            LicensePlateId = site.PlateId, FromLocationId = site.Dock.Id, ToLocationId = site.Wide.Id,
            IdempotencyKey = Guid.NewGuid().ToString(), TaskId = Guid.NewGuid(), DecisionTraceId = Guid.NewGuid(),
            Actor = Actor, ExpectedContents = expected
        };
        var fault = new FailOutcomeSettlement(command.TaskId);
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<InventoryDbContext>(options => options.AddInterceptors(fault))));
        using (var scope = app.Services.CreateScope())
            await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<IStockPosting>()
                .PutawayLicensePlateAsync(command, CancellationToken.None));
        Assert.True(fault.SawInsertedMovements);
        var db = setup.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.False(await db.PlatePutawayCommands.AnyAsync(row => row.IdempotencyKey == command.IdempotencyKey));
        Assert.Equal(0, await db.StockMovements.CountAsync(row => row.TaskId == command.TaskId));
        Assert.Empty(await setup.ServiceProvider.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Wide.Id, CancellationToken.None));
        var retried = await setup.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(command, CancellationToken.None);
        Assert.True(retried.IsSuccess);
        Assert.All(retried.Value, movement => Assert.False(movement.WasAlreadyPosted));
        Assert.Equal(2, await db.StockMovements.CountAsync(row => row.TaskId == command.TaskId));
    }

    private sealed class FailOutcomeSettlement(Guid taskId) : DbCommandInterceptor
    {
        public bool SawInsertedMovements { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("PlatePutawayCommands", StringComparison.Ordinal))
            {
                await using var count = command.Connection!.CreateCommand();
                count.Transaction = command.Transaction;
                count.CommandText = """SELECT COUNT(*) FROM inventory."StockMovements" WHERE "TaskId" = @task""";
                var parameter = count.CreateParameter(); parameter.ParameterName = "task"; parameter.Value = taskId;
                count.Parameters.Add(parameter);
                SawInsertedMovements = (long)(await count.ExecuteScalarAsync(cancellationToken))! == 2;
                throw new IOException("Simulated interruption before command outcome settlement");
            }
            return result;
        }
    }

    private sealed class LoseCommandCommitAcknowledgement : DbTransactionInterceptor
    {
        private int _remaining = 1;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _remaining, 0) == 1) throw new IOException("Simulated lost Inventory commit acknowledgement");
            return Task.CompletedTask;
        }
    }

    private sealed class PausePalletRead(Guid plateId) : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("StockBalances", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, plateId))
                && Reached.TrySetResult(true))
                await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private static PalletPutawayQuery Query(Site site, IReadOnlyList<PlateLine> contents) => new()
    {
        WarehouseId = site.Warehouse.Id, LicensePlateId = site.PlateId,
        FromLocationId = site.Dock.Id, Contents = contents, Actor = Actor
    };

    private static async Task<IReadOnlyList<PlateLine>> ReceivePalletAsync(IServiceProvider services, Site site,
        bool quarantineSecond = false)
    {
        var posting = services.GetRequiredService<IStockPosting>();
        foreach (var item in new[] { site.First, site.Second })
        {
            var receipt = await posting.ReceiveAsync(new ReceiptInstruction
            {
                ItemId = item.Id, Quantity = 6m, ToLocationId = site.Dock.Id, Sscc = site.Sscc,
                Condition = quarantineSecond && item.Id == site.Second.Id ? StockCondition.Quarantine : StockCondition.Available,
                IdempotencyKey = Guid.NewGuid().ToString(), Actor = Actor
            }, CancellationToken.None);
            Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.ToString() : "");
            site.PlateId = receipt.Value.LicensePlateId!.Value;
        }
        return await services.GetRequiredService<IStockQuery>().PlateContentsAsync(site.PlateId, site.Dock.Id, CancellationToken.None);
    }

    private static async Task<Site> NewSiteAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MasterDataDbContext>();
        var code = "PAL-" + Guid.NewGuid().ToString("N")[..10];
        var warehouse = new Warehouse { Code = code, Name = code };
        var area = new Area { WarehouseId = warehouse.Id, Code = "STOR", Name = "Storage" };
        var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
        var dock = new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id,
            Code = code + "-DOCK", Type = LocationType.ReceivingDock };
        var small = new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id,
            Code = code + "-SMALL", Type = LocationType.Bulk, MaxVolumeM3 = 10m,
            MaxWeightKg = 100m, MaxDistinctItems = 1, PickSequence = 1 };
        var wide = new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id,
            Code = code + "-WIDE", Type = LocationType.Bulk, MaxVolumeM3 = 20m,
            MaxWeightKg = 100m, MaxDistinctItems = 3, PickSequence = 2 };
        var supplier = new Location { WarehouseId = warehouse.Id, Code = code + "-SUP",
            Type = LocationType.Supplier };
        Item NewItem(string suffix)
        {
            var item = new Item { Sku = code + suffix, Description = code, BaseUom = "EA", AbcClass = AbcClass.B };
            item.PackConfigurations.Add(new PackConfiguration { ItemId = item.Id, Level = PackagingLevel.Each,
                BaseUnitQuantity = 1, LengthMm = 1000m, WidthMm = 1000m, HeightMm = 1000m,
                GrossWeightKg = 1m, IsPickUnit = true });
            return item;
        }
        var first = NewItem("-A");
        var second = NewItem("-B");
        db.Warehouses.Add(warehouse); db.Areas.Add(area); db.Zones.Add(zone);
        db.Locations.AddRange(dock, small, wide, supplier); db.Items.AddRange(first, second);
        await db.SaveChangesAsync();
        var body = "000123456" + Random.Shared.NextInt64(10000000, 99999999).ToString();
        return new Site(warehouse, dock, small, wide, first, second, Sscc.FromPayload(body));
    }

    private sealed record Site(Warehouse Warehouse, Location Dock, Location Small, Location Wide,
        Item First, Item Second, string Sscc)
    {
        public Guid PlateId { get; set; }
    }
}
