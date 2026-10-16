using System.Data.Common;
using Countflow.Modules.Work.Domain;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Countflow.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Countflow.Modules.Inventory.Integration;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Countflow.IntegrationTests;

public sealed partial class InboundLoopTests
{
    [Fact]
    public async Task A_pallet_completion_survives_its_work_session_loss_and_blocks_competing_mutations()
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync();
        var other = await NewItemAsync();
        var sscc = NewSscc();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = site.Dock, sscc }));
        var revision = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Ready to move" }));
        var trace = revision.GetProperty("current").GetProperty("task").GetProperty("decisionTraceId").GetGuid();
        var gate = new CompletionGate();
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<StockPosting>();
            services.Replace(ServiceDescriptor.Scoped<IStockPosting>(provider => new PausedPalletPosting(
                provider.GetRequiredService<StockPosting>(), provider.GetRequiredService<WorkDbContext>(), gate)));
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var body = new { toLocationCode = receipt.SuggestedLocationCode, expectedDecisionTraceId = trace };
        var pending = PostAsync(client, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body);
        try
        {
            var pid = await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var scope = fixture.Services.CreateScope();
            await using var connection = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<WorkDbContext>().Database.GetConnectionString());
            await connection.OpenAsync();
            await using (var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection))
            {
                kill.Parameters.AddWithValue("pid", pid);
                Assert.True((bool)(await kill.ExecuteScalarAsync())!);
            }
            var current = await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}");
            Assert.True(current.GetProperty("task").TryGetProperty("completionPending", out var flag) && flag.GetBoolean());
            async Task Refused(string path, object request)
            {
                var response = await PostAsync(_supervisor, path, request);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("work.completion_pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
            await Refused($"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Must not overtake confirmation" });
            await Refused($"/api/v1/work/tasks/{receipt.WorkTaskId}/claim", new { });
            await Refused("/api/v1/inbound/receipts", new { sku = other, quantity = 1m, toLocationCode = site.Dock, sscc });
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(_operator, "/api/v1/work/tasks/claim-next",
                new { warehouseId = site.WarehouseId, kind = "Putaway" })).StatusCode);
            var cancel = await scope.ServiceProvider.GetRequiredService<IWorkQueue>().CancelAsync([receipt.WorkTaskId!.Value], "Must wait", CancellationToken.None);
            Assert.True(cancel.IsFailure);
            Assert.Equal("work.completion_pending", cancel.Error.Code);
            var frozenTask = await scope.ServiceProvider.GetRequiredService<WorkDbContext>().WorkTasks
                .AsNoTracking().SingleAsync(row => row.Id == receipt.WorkTaskId);
            var append = await scope.ServiceProvider.GetRequiredService<IWorkQueue>().EnqueueAsync(new WorkTaskRequest
            {
                Kind = WorkTaskKind.Putaway, WarehouseId = frozenTask.WarehouseId, ItemId = frozenTask.ItemId,
                Quantity = 1m, FromLocationId = frozenTask.FromLocationId, LicensePlateId = frozenTask.LicensePlateId,
                IdempotencyKey = Guid.NewGuid().ToString()
            }, CancellationToken.None);
            Assert.True(append.IsFailure);
            Assert.Equal("work.completion_pending", append.Error.Code);
            gate.Resume.TrySetResult(true);
            var done = await Read<TaskCompletion>(await pending);
            Assert.Equal("Completed", done.Task.Status);
            Assert.Equal(10m, Assert.Single(done.Movements).Quantity);
            Assert.Equal(0m, await BalanceAsync(sku, site.Dock));
            Assert.Equal(0m, await BalanceAsync(other, site.Dock));
        }
        finally { gate.Resume.TrySetResult(true); await pending; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_interrupted_pallet_confirmation_resumes_its_original_body_and_frozen_actor(bool afterPosting)
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync(caller: $"Complete-loss-{afterPosting}");
        var sscc = NewSscc();
        var receiptBody = new { sku, quantity = 10m, toLocationCode = site.Dock, sscc };
        var receiptKey = Guid.NewGuid().ToString();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", receiptBody, receiptKey));
        var revised = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Unload finished" }));
        var trace = revised.GetProperty("current").GetProperty("task").GetProperty("decisionTraceId").GetGuid();
        await Read<TaskDetail>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/claim", new { }));
        var actual = receipt.SuggestedLocationCode == site.Bin ? site.Alternate : site.Bin;
        var body = new { toLocationCode = actual, overrideReason = "Original floor decision", expectedDecisionTraceId = trace };
        var key = Guid.NewGuid().ToString();
        var gate = new CompletionGate { ThrowBeforePosting = !afterPosting, ThrowAfterPosting = afterPosting };
        gate.Resume.TrySetResult(true);
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<StockPosting>();
            services.Replace(ServiceDescriptor.Scoped<IStockPosting>(provider => new PausedPalletPosting(
                provider.GetRequiredService<StockPosting>(), provider.GetRequiredService<WorkDbContext>(), gate)));
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(client, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key)).StatusCode);
        var pending = await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}");
        Assert.True(pending.GetProperty("task").GetProperty("completionPending").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/release", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_secondOperator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key)).StatusCode);
        var changed = await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = actual, overrideReason = "Changed reason", expectedDecisionTraceId = trace }, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("work.completion_key_conflict", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body)).StatusCode);
        // Acknowledged receipt retry is harmless, but must not direct another move.
        var receiptRetry = await Read<JsonElement>(await PostAsync(_operator, "/api/v1/inbound/receipts", receiptBody, receiptKey));
        Assert.True(receiptRetry.GetProperty("completionPending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, receiptRetry.GetProperty("suggestedLocationCode").ValueKind);
        TaskCompletion done;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
            var user = (await users.FindByNameAsync("inbound-operator"))!;
            var originalName = user.UserName!;
            Assert.True((await users.SetUserNameAsync(user, "renamed-" + Guid.NewGuid().ToString("N"))).Succeeded);
            try
            {
                using var renamed = await SignInAsync(user.UserName!);
                done = await Read<TaskCompletion>(await PostAsync(renamed, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key));
            }
            finally { Assert.True((await users.SetUserNameAsync(user, originalName)).Succeeded); }
        }
        Assert.Equal("Completed", done.Task.Status);
        Assert.True(done.Task.WasOverridden);
        Assert.Equal(actual, done.Task.CompletedAtLocationCode);
        Assert.Equal(10m, Assert.Single(done.Movements).Quantity);
        var final = await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}");
        Assert.False(final.GetProperty("task").GetProperty("completionPending").GetBoolean());
        using (var check = fixture.Services.CreateScope())
        {
            var work = check.ServiceProvider.GetRequiredService<WorkDbContext>();
            Assert.Equal("inbound-operator", (await work.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == receipt.WorkTaskId)).CompletedByUserName);
            var stock = check.ServiceProvider.GetRequiredService<InventoryDbContext>();
            Assert.Equal("inbound-operator", (await stock.StockMovements.AsNoTracking().SingleAsync(row => row.TaskId == receipt.WorkTaskId)).UserName);
        }
        Assert.Equal("Original floor decision", final.GetProperty("task").GetProperty("overrideReason").GetString());
        Assert.Equal(trace, Assert.Single((await MovementsAsync(sku)), movement => movement.Type == "Putaway").DecisionTraceId);
        Assert.Equal(2, (await MovementsAsync(sku)).Count);
        var replay = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key));
        Assert.Empty(replay.Movements);
    }

    [Theory]
    [InlineData("prepare-after")]
    [InlineData("settle-before")]
    [InlineData("settle-after")]
    public async Task Work_commit_interruptions_recover_without_losing_or_reposting_the_pallet(string stage)
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync(caller: $"Complete-{stage}");
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = site.Dock, sscc = NewSscc() }));
        var fault = new CompletionCommitFault(stage);
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<WorkDbContext>(options => options.AddInterceptors(fault))));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var body = new { toLocationCode = receipt.SuggestedLocationCode };
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(client, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key)).StatusCode);
        Assert.True(fault.Fired);
        var task = (await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}")).GetProperty("task");
        Assert.Equal(stage != "settle-after", task.GetProperty("completionPending").GetBoolean());
        Assert.Equal(stage == "prepare-after" ? 10m : 0m, await BalanceAsync(sku, site.Dock));
        var done = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key));
        Assert.Equal("Completed", done.Task.Status);
        Assert.Single(await MovementsAsync(sku), movement => movement.Type == "Putaway");
        Assert.Equal(0m, await BalanceAsync(sku, site.Dock));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var intent = await db.PutawayCompletionIntents.AsNoTracking().SingleAsync(row => row.WorkTaskId == receipt.WorkTaskId);
        Assert.Equal(PutawayCompletionState.Succeeded, intent.State);
        Assert.Null((await db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == receipt.WorkTaskId)).PendingPutawayCompletionId);
    }

    [Fact]
    public async Task A_terminal_contents_refusal_releases_work_but_never_reuses_its_old_confirmation()
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync();
        var sscc = NewSscc();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = site.Dock, sscc }));
        var revision = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Finish unloading" }));
        var task = revision.GetProperty("current").GetProperty("task");
        // A direct Inventory writer bypasses the receipt handoff projection.
        using (var scope = fixture.Services.CreateScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var item = await directory.Items.SingleAsync(row => row.Sku == sku);
            var dock = await directory.Locations.SingleAsync(row => row.Code == site.Dock);
            var posted = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
            {
                ItemId = item.Id, Quantity = 1m, ToLocationId = dock.Id, Sscc = sscc,
                IdempotencyKey = Guid.NewGuid().ToString(), Actor = new PostingActor(Guid.NewGuid(), "external")
            }, CancellationToken.None);
            Assert.True(posted.IsSuccess);
        }
        var body = new { toLocationCode = receipt.SuggestedLocationCode,
            expectedDecisionTraceId = task.GetProperty("decisionTraceId").GetGuid() };
        var key = Guid.NewGuid().ToString();
        var fault = new CompletionCommitFault("settle-before");
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<WorkDbContext>(options => options.AddInterceptors(fault))));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(client, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key)).StatusCode);
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_supervisor, $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Still pending" })).StatusCode);
        var refused = await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var after = (await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}")).GetProperty("task");
        Assert.False(after.GetProperty("completionPending").GetBoolean());
        Assert.True(after.GetProperty("putawayAdviceStale").GetBoolean());
        var revised = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Account for extra stock" }));
        var current = revised.GetProperty("current").GetProperty("task");
        var fresh = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = current.GetProperty("suggestedLocationCode").GetString(),
                expectedDecisionTraceId = current.GetProperty("decisionTraceId").GetGuid() }));
        Assert.Equal(11m, Assert.Single(fresh.Movements).Quantity);
        var late = await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("inventory.pallet_contents_changed", (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Single(await MovementsAsync(sku), movement => movement.Type == "Putaway");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_legacy_inventory_success_can_finish_work_without_a_reconstructed_empty_snapshot(bool revised)
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync(caller: $"Complete-legacy-{revised}");
        var sscc = NewSscc();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = site.Dock, sscc }));
        if (revised) await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Legacy snapshot" }));
        var detail = (await GetAsync<JsonElement>($"/api/v1/work/tasks/{receipt.WorkTaskId}")).GetProperty("task");
        var trace = detail.GetProperty("decisionTraceId").GetGuid();
        var destination = detail.GetProperty("suggestedLocationCode").GetString()!;
        var wrong = destination == site.Bin ? site.Alternate : site.Bin;
        var key = Guid.NewGuid().ToString();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            var task = await db.WorkTasks.AsNoTracking().SingleAsync(row => row.Id == receipt.WorkTaskId);
            var user = (await scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>().FindByNameAsync("inbound-operator"))!;
            var target = (await scope.ServiceProvider.GetRequiredService<ILocationDirectory>().FindByCodeAsync(destination, CancellationToken.None))!;
            var contents = await scope.ServiceProvider.GetRequiredService<IStockQuery>().PlateContentsAsync(task.LicensePlateId!.Value, task.FromLocationId, CancellationToken.None);
            var posted = await scope.ServiceProvider.GetRequiredService<IStockPosting>().PutawayLicensePlateAsync(new PlatePutawayInstruction
            {
                LicensePlateId = task.LicensePlateId.Value, FromLocationId = task.FromLocationId, ToLocationId = target.Id,
                TaskId = task.Id, DecisionTraceId = trace, IdempotencyKey = key,
                Actor = new PostingActor(user.Id, user.UserName!), ExpectedContents = revised ? contents : null
            }, CancellationToken.None);
            Assert.True(posted.IsSuccess);
            // Simulate an older Inventory binary which did not have this journal.
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().PlatePutawayCommands
                .Where(row => row.IdempotencyKey == key).ExecuteDeleteAsync();
        }
        var conflict = await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = wrong, expectedDecisionTraceId = trace }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var done = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = destination, expectedDecisionTraceId = trace }, key));
        Assert.Equal("Completed", done.Task.Status);
        Assert.Single(await MovementsAsync(sku), movement => movement.Type == "Putaway");
    }

    [Fact]
    public async Task Simultaneous_same_key_pallet_finishers_settle_one_intent_and_one_stock_batch()
    {
        var site = await NewCompletionSiteAsync();
        var sku = await NewItemAsync();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = site.Dock, sscc = NewSscc() }));
        var key = Guid.NewGuid().ToString();
        var body = new { toLocationCode = receipt.SuggestedLocationCode };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(_operator,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete", body, key)));
        foreach (var response in responses) Assert.Equal("Completed", (await Read<TaskCompletion>(response)).Task.Status);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().PutawayCompletionIntents.CountAsync(row => row.WorkTaskId == receipt.WorkTaskId));
        Assert.Single(await MovementsAsync(sku), movement => movement.Type == "Putaway");
    }

    private sealed class CompletionCommitFault(string stage) : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        private bool HasState(DbContext? context, PutawayCompletionState state) => !Fired && context is WorkDbContext db
            && db.ChangeTracker.Entries<PutawayCompletionIntent>().Any(entry => entry.Entity.State == state);
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (stage == "settle-before" && (HasState(eventData.Context, PutawayCompletionState.Succeeded)
                || HasState(eventData.Context, PutawayCompletionState.Refused)))
            { Fired = true; throw new IOException("Simulated Work failure before settlement commit"); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if ((stage == "prepare-after" && HasState(eventData.Context, PutawayCompletionState.Pending))
                || (stage == "settle-after" && HasState(eventData.Context, PutawayCompletionState.Succeeded)))
            { Fired = true; throw new IOException("Simulated lost Work commit acknowledgement"); }
            return Task.CompletedTask;
        }
    }

    private async Task<(Guid WarehouseId, string Dock, string Bin, string Alternate)> NewCompletionSiteAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var code = "ZZCMP-" + Guid.NewGuid().ToString("N")[..10];
        var warehouse = new Warehouse { Code = code, Name = code };
        var area = new Area { WarehouseId = warehouse.Id, Code = "STOR", Name = "Storage" };
        var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
        var dock = new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id, Code = code + "-DOCK", Type = LocationType.ReceivingDock };
        db.Warehouses.Add(warehouse); db.Areas.Add(area); db.Zones.Add(zone);
        db.Locations.AddRange(dock,
            new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id, Code = code + "-BIN", Type = LocationType.Bulk, MaxVolumeM3 = 20m, MaxWeightKg = 1000m, MaxDistinctItems = 3, PickSequence = 1 },
            new Location { WarehouseId = warehouse.Id, ZoneId = zone.Id, Code = code + "-ALT", Type = LocationType.Bulk, MaxVolumeM3 = 20m, MaxWeightKg = 1000m, MaxDistinctItems = 3, PickSequence = 2 },
            new Location { WarehouseId = warehouse.Id, Code = code + "-SUP", Type = LocationType.Supplier });
        await db.SaveChangesAsync();
        return (warehouse.Id, dock.Code, code + "-BIN", code + "-ALT");
    }

    private sealed class CompletionGate
    {
        public bool ThrowBeforePosting { get; init; }
        public bool ThrowAfterPosting { get; init; }
        public TaskCompletionSource<int> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausedPalletPosting(IStockPosting inner, WorkDbContext work, CompletionGate gate) : IStockPosting
    {
        public async Task<Result<IReadOnlyList<PostedMovement>>> PutawayLicensePlateAsync(PlatePutawayInstruction instruction, CancellationToken ct)
        {
            // Keep a physical Work session available to terminate, even after
            // preparation correctly commits and releases its transaction.
            await work.Database.OpenConnectionAsync(ct);
            gate.Reached.TrySetResult(((NpgsqlConnection)work.Database.GetDbConnection()).ProcessID);
            await gate.Resume.Task.WaitAsync(ct);
            await work.Database.CloseConnectionAsync();
            if (gate.ThrowBeforePosting) throw new IOException("Simulated interruption before Inventory posting");
            var result = await inner.PutawayLicensePlateAsync(instruction, ct);
            if (gate.ThrowAfterPosting) throw new IOException("Simulated interruption after Inventory posting");
            return result;
        }
        public Task<Result<PostedMovement>> ReceiveAsync(ReceiptInstruction i, CancellationToken ct) => inner.ReceiveAsync(i, ct);
        public Task<Result<PostedMovement>> PutawayAsync(PutawayInstruction i, CancellationToken ct) => inner.PutawayAsync(i, ct);
        public Task<Result<PostedMovement>> PickAsync(PickInstruction i, CancellationToken ct) => inner.PickAsync(i, ct);
        public Task<Result<PostedMovement>> ReplenishAsync(PickInstruction i, CancellationToken ct) => inner.ReplenishAsync(i, ct);
        public Task<Result<IReadOnlyList<PostedMovement>>> DespatchAsync(DespatchInstruction i, CancellationToken ct) => inner.DespatchAsync(i, ct);
        public Task<Result<CountOutcome>> CountAsync(CountInstruction i, CancellationToken ct) => inner.CountAsync(i, ct);
        public Task<Result<PostedMovement>> ScrapAsync(ScrapInstruction i, CancellationToken ct) => inner.ScrapAsync(i, ct);
        public Task<Result<PostedMovement>> ChangeConditionAsync(ConditionChangeInstruction i, CancellationToken ct) => inner.ChangeConditionAsync(i, ct);
        public Task<Result<PostedMovement>> SendForRepairAsync(RepairInstruction i, CancellationToken ct) => inner.SendForRepairAsync(i, ct);
    }
}
