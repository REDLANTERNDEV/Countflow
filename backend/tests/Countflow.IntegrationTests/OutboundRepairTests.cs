using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Countflow.Modules.Outbound.Domain;
using Npgsql;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.Outbound.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed partial class OutboundLoopTests
{
    [Fact]
    public async Task Repair_rehomes_only_the_stranded_line_and_replays_without_more_claims_or_picks()
    {
        var sku = await NewItemAsync();
        var source = await ReceiveAndPutAwayAsync(sku, 30m);
        var otherSku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(otherSku, 10m);
        var order = await Read<OrderBody>(await PostAsync(_supervisor, "/api/v1/outbound/orders", new
        {
            warehouseCode = _warehouseCode, reference = NewOrderReference(), stagingLocationCode = _bench,
            lines = new[] { new { sku, quantity = 20m }, new { sku = otherSku, quantity = 10m } }
        }));
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single(row => row.Sku == sku);
        var otherBefore = released.Order.Lines.Single(row => row.Sku == otherSku).Allocations.Single();
        var original = line.Allocations.Single();
        using (var scope = fixture.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var alternate = await master.Locations.Where(location => location.IsPickable && location.IsActive
                && location.Code != source).OrderBy(location => location.Code).Select(location => location.Code).FirstAsync();
            await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
                new { sku, quantity = 30m, fromLocationCode = source, toLocationCode = alternate }));
        }
        var key = Guid.CreateVersion7().ToString();
        var url = $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair";
        var repaired = await Read<RepairResult>(await PostAsync(_supervisor, url, new { reason = "Stock relocated" }, key));
        Assert.Equal(20m, repaired.Order.Lines.Single(row => row.Sku == sku).AllocatedQuantity);
        Assert.Equal(0m, repaired.ShortfallQuantity);
        Assert.Equal(1, repaired.ReservationsReleased);
        Assert.Equal(1, repaired.TasksCancelled);
        Assert.Equal(1, repaired.TasksRaised);
        Assert.False(repaired.WasAlreadyRepaired);
        Assert.Equal("Cancelled", repaired.Order.Lines.Single(row => row.Sku == sku).Allocations.Single(row => row.Id == original.Id).TaskState);
        var replacement = Assert.Single(repaired.Order.Lines.Single(row => row.Sku == sku).Allocations, row => row.Id != original.Id);
        Assert.NotEqual(source, replacement.LocationCode);
        Assert.NotEqual(original.DecisionTraceId, replacement.DecisionTraceId);

        var replay = await Read<RepairResult>(await PostAsync(_supervisor, url, new { reason = "Stock relocated" }, key));
        Assert.True(replay.WasAlreadyRepaired);
        Assert.Equal(repaired.TasksRaised, replay.TasksRaised);
        Assert.Equal(2, replay.Order.Lines.Single(row => row.Sku == sku).Allocations.Count);
        var otherAfter = repaired.Order.Lines.Single(row => row.Sku == otherSku).Allocations.Single();
        Assert.Equal(otherBefore, otherAfter);
        foreach (var allocation in repaired.Order.Lines.SelectMany(row => row.Allocations).Where(row => row.TaskState == "Ready"))
        {
            await Read<TaskDetail>(await PostAsync(_operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/claim", new { }));
            await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/complete", new { toLocationCode = _bench }));
        }
        await Read<OrderBody>(await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/pack", new { }));
        var shipped = await Read<DespatchResult>(await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/despatch", new { }));
        Assert.Equal("Despatched", shipped.Order.Status);
        Assert.Equal(30m, shipped.Movements.Sum(row => row.Quantity));
        var completedReplay = await Read<RepairResult>(await PostAsync(_supervisor, url, new { reason = "Stock relocated" }, key));
        Assert.True(completedReplay.WasAlreadyRepaired);
        Assert.Equal("Despatched", completedReplay.Order.Status);
        var reused = await PostAsync(_supervisor, url, new { reason = "Changed reason" }, key);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("outbound.idempotency_key_reused", (await reused.Content.ReadFromJsonAsync<RepairProblem>())!.Code);
    }

    [Fact]
    public async Task Repair_refuses_a_claimed_pick_and_preserves_its_open_reservation()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m);
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single();
        var taskId = line.Allocations.Single().WorkTaskId;
        await Read<TaskDetail>(await PostAsync(_operator, $"/api/v1/work/tasks/{taskId}/claim", new { }));
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
            new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = _dock }));
        var response = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair",
            new { reason = "Count discrepancy" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("outbound.picks_in_progress", (await response.Content.ReadFromJsonAsync<RepairProblem>())!.Code);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(1, await db.StockReservations.CountAsync(row => row.OwnerId == line.Id
            && row.State == Countflow.Modules.Inventory.Domain.ReservationState.Open));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Repair_reports_real_shortfall_and_a_later_release_can_chase_it(int remaining)
    {
        var marker = $"repair-short-{remaining}-{Guid.CreateVersion7():N}";
        var sku = await NewItemAsync(caller: marker);
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m, caller: marker);
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single();
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
            new { sku, quantity = 10m - remaining, fromLocationCode = bin, toLocationCode = _dock }));
        var repaired = await Read<RepairResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair", new { reason = "Missing goods" }));
        Assert.Equal(remaining, repaired.Order.Lines.Single().AllocatedQuantity);
        Assert.Equal(10m - remaining, repaired.ShortfallQuantity);
        Assert.Equal(remaining == 0 ? 0 : 1, repaired.TasksRaised);
        await ReceiveAndPutAwayAsync(sku, 10m - remaining);
        var caughtUp = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        Assert.Equal(10m, caughtUp.Order.Lines.Single().AllocatedQuantity);
        Assert.Equal(0m, caughtUp.Order.Lines.Single().OutstandingQuantity);
    }

    [Theory]
    [InlineData("withdrawal")]
    [InlineData("reservation")]
    [InlineData("task")]
    [InlineData("receipt")]
    public async Task Repair_resumes_after_a_lost_cross_module_acknowledgement(string failurePoint)
    {
        var marker = $"{failurePoint}-{Guid.CreateVersion7():N}";
        var sku = await NewItemAsync(caller: marker);
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m, caller: marker);
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single();
        using (var scope = fixture.Services.CreateScope())
        {
            var alternate = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations
                .Where(location => location.IsPickable && location.IsActive && location.Code != bin)
                .OrderBy(location => location.Code).Select(location => location.Code).FirstAsync();
            await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
                new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = alternate }));
        }
        var interceptor = new FailRepairSave(failurePoint);
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OutboundDbContext>(options => options.AddInterceptors(interceptor))));
        using var interrupted = host.CreateClient();
        interrupted.DefaultRequestHeaders.Authorization = _supervisor.DefaultRequestHeaders.Authorization;
        var key = Guid.CreateVersion7().ToString();
        var url = $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair";
        Assert.Equal(HttpStatusCode.InternalServerError,
            (await PostAsync(interrupted, url, new { reason = "Relocate" }, key)).StatusCode);
        var blocked = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("outbound.repair_in_progress", (await blocked.Content.ReadFromJsonAsync<RepairProblem>())!.Code);
        var repaired = await Read<RepairResult>(await PostAsync(_supervisor, url, new { reason = "Relocate" }, key));
        Assert.Equal(0m, repaired.ShortfallQuantity);
        Assert.Equal(1, repaired.ReservationsReleased);
        Assert.Equal(1, repaired.TasksCancelled);
        Assert.Equal(1, repaired.AllocationsRaised);
        Assert.Equal(1, repaired.TasksRaised);
        Assert.Equal(2, repaired.Order.Lines.Single().Allocations.Count);
        using var check = fixture.Services.CreateScope();
        Assert.Equal(2, await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations
            .CountAsync(row => row.OwnerId == line.Id));
        var cancelled = await Read<CancelResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/cancel", new { reason = "Customer withdrew order" }));
        Assert.Equal("Cancelled", cancelled.Order.Status);
        Assert.Equal(1, cancelled.ReservationsReleased);
    }

    [Fact]
    public async Task Repair_serializes_simultaneous_retries_and_refuses_operator_access()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m);
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single();
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
            new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = _dock }));
        var url = $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair";
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_operator, url, new { reason = "Not allowed" })).StatusCode);
        var key = Guid.CreateVersion7().ToString();
        var calls = await Task.WhenAll(PostAsync(_supervisor, url, new { reason = "Missing" }, key),
            PostAsync(_supervisor, url, new { reason = "Missing" }, key));
        var results = await Task.WhenAll(calls.Select(Read<RepairResult>));
        Assert.Single(results, row => row.WasAlreadyRepaired);
        Assert.Single(results, row => !row.WasAlreadyRepaired);
        Assert.All(results, row => Assert.Equal(10m, row.ShortfallQuantity));
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboundDbContext>().OrderLineRepairs
            .CountAsync(row => row.SalesOrderLineId == line.Id));
        var redundant = await PostAsync(_supervisor, url, new { reason = "Another attempt" });
        Assert.Equal(HttpStatusCode.Conflict, redundant.StatusCode);
        Assert.Equal("outbound.no_stranded_commitment", (await redundant.Content.ReadFromJsonAsync<RepairProblem>())!.Code);
    }

    [Fact]
    public async Task Repair_recovers_an_unrecorded_pick_before_deciding_whether_it_is_claimed()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m);
        var released = await Read<ReleaseResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/orders/{order.Id}/release", new { }));
        var line = released.Order.Lines.Single();
        var allocation = line.Allocations.Single();
        // The persisted state after Work accepted an enqueue but Outbound lost
        // its acknowledgement before saving WorkTaskId.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutboundDbContext>();
            (await db.OrderLineAllocations.SingleAsync(row => row.Id == allocation.Id)).WorkTaskId = null;
            await db.SaveChangesAsync();
            // Older releases could create the same reservation's task under a
            // later retry key. Recovery must use identity, not guess that key.
            var legacyKey = $"legacy-release-retry:{Guid.CreateVersion7():N}";
            await scope.ServiceProvider.GetRequiredService<Countflow.Modules.Work.Persistence.WorkDbContext>()
                .WorkTaskSources.Where(row => row.WorkTaskId == allocation.WorkTaskId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.IdempotencyKey, legacyKey));
        }
        await Read<TaskDetail>(await PostAsync(_operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/claim", new { }));
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
            new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = _dock }));
        var response = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair", new { reason = "Investigate missing stock" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("outbound.picks_in_progress", (await response.Content.ReadFromJsonAsync<RepairProblem>())!.Code);
        using var check = fixture.Services.CreateScope();
        Assert.Equal(allocation.WorkTaskId, (await check.ServiceProvider.GetRequiredService<OutboundDbContext>()
            .OrderLineAllocations.SingleAsync(row => row.Id == allocation.Id)).WorkTaskId);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations.CountAsync(row =>
            row.OwnerId == line.Id && row.State == Countflow.Modules.Inventory.Domain.ReservationState.Open));
    }

    [Fact]
    public async Task A_release_cannot_enqueue_after_its_locked_session_dies_and_another_request_cancels_the_order()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m);
        var pause = new PauseAfterAllocationSave();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OutboundDbContext>(options => options.AddInterceptors(pause))));
        using var delayed = host.CreateClient();
        delayed.DefaultRequestHeaders.Authorization = _supervisor.DefaultRequestHeaders.Authorization;
        var original = PostAsync(delayed, $"/api/v1/outbound/orders/{order.Id}/release", new { });
        try
        {
            var processId = await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using (var scope = fixture.Services.CreateScope())
            {
                await using var connection = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<OutboundDbContext>().Database.GetConnectionString());
                await connection.OpenAsync();
                await using var terminate = connection.CreateCommand();
                terminate.CommandText = "SELECT pg_terminate_backend(@pid)";
                terminate.Parameters.AddWithValue("pid", processId);
                Assert.Equal(true, await terminate.ExecuteScalarAsync());
            }
            var cancelled = await Read<CancelResult>(await PostAsync(_supervisor,
                $"/api/v1/outbound/orders/{order.Id}/cancel", new { reason = "Cancelled during interrupted release" }));
            Assert.Equal("Cancelled", cancelled.Order.Status);
            pause.Resume.TrySetResult(true);
            Assert.Equal(HttpStatusCode.InternalServerError, (await original).StatusCode);
            using var check = fixture.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<OutboundDbContext>();
            var allocation = await db.OrderLineAllocations.SingleAsync(row => row.Line!.SalesOrderId == order.Id);
            var task = await check.ServiceProvider.GetRequiredService<Countflow.Modules.Work.Persistence.WorkDbContext>()
                .WorkTasks.SingleAsync(row => row.StockReservationId == allocation.StockReservationId);
            Assert.Equal(Countflow.Modules.Work.Domain.WorkTaskStatus.Cancelled, task.Status);
            Assert.Equal(Countflow.Modules.Inventory.Domain.ReservationState.Released,
                (await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations.FindAsync(allocation.StockReservationId))!.State);
        }
        finally { pause.Resume.TrySetResult(true); await original; }
    }

    [Fact]
    public async Task A_wave_cannot_change_the_staging_location_or_notes_of_an_order_with_a_pending_repair()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        var order = await CreateOrderAsync(sku, 10m);
        var wave = await Read<WaveBody>(await PostAsync(_supervisor, "/api/v1/outbound/waves", new
        { reference = NewOrderReference(), orderIds = new[] { order.Id }, warehouseCode = _warehouseCode }));
        var released = await Read<ReleaseWaveResult>(await PostAsync(_supervisor,
            $"/api/v1/outbound/waves/{wave.Id}/release", new { }));
        var line = released.Wave.Orders.Single().Lines.Single();
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/transfers",
            new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = _dock }));
        var interceptor = new FailRepairSave("withdrawal");
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OutboundDbContext>(options => options.AddInterceptors(interceptor))));
        using var interrupted = host.CreateClient();
        interrupted.DefaultRequestHeaders.Authorization = _supervisor.DefaultRequestHeaders.Authorization;
        var key = Guid.CreateVersion7().ToString();
        var repairUrl = $"/api/v1/outbound/orders/{order.Id}/lines/{line.Id}/repair";
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(interrupted, repairUrl, new { reason = "Missing" }, key)).StatusCode);
        using (var scope = fixture.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var otherBench = await master.Locations.Where(location => location.Type == Countflow.Modules.MasterData.Domain.LocationType.Packing
                && location.Code != _bench).Select(location => location.Code).FirstAsync();
            var attempt = await Read<ReleaseWaveResult>(await PostAsync(_supervisor,
                $"/api/v1/outbound/waves/{wave.Id}/release", new { stagingLocationCode = otherBench }));
            Assert.Equal(0, attempt.OrdersReleased);
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<OutboundDbContext>().SalesOrders.SingleAsync(row => row.Id == order.Id);
            Assert.Null(saved.Notes);
            var actualBench = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations
                .Where(row => row.Id == saved.StagingLocationId).Select(row => row.Code).SingleAsync();
            Assert.Equal(_bench, actualBench);
        }
        await Read<RepairResult>(await PostAsync(_supervisor, repairUrl, new { reason = "Missing" }, key));
    }

    private sealed class PauseAfterAllocationSave : SaveChangesInterceptor
    {
        public TaskCompletionSource<int> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pause;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _pause = eventData.Context!.ChangeTracker.Entries<OrderLineAllocation>().Any(entry => entry.State == EntityState.Added);
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_pause && Reached.TrySetResult(((NpgsqlConnection)eventData.Context!.Database.GetDbConnection()).ProcessID))
                await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class FailRepairSave(string point) : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var allocations = db.ChangeTracker.Entries<OrderLineAllocation>();
            var shouldFail = point switch
            {
                "withdrawal" => allocations.Any(entry => entry.State == EntityState.Modified && entry.Entity.SupersededByRepairId != null),
                "reservation" => allocations.Any(entry => entry.State == EntityState.Added && entry.Entity.ReleaseIdempotencyKey.StartsWith("repair:")),
                "task" => allocations.Any(entry => entry.State == EntityState.Modified && entry.Entity.ReleaseIdempotencyKey.StartsWith("repair:") && entry.Entity.WorkTaskId != null),
                "receipt" => db.ChangeTracker.Entries<OrderLineRepair>().Any(entry => entry.State == EntityState.Modified && entry.Entity.CompletedAt != null),
                _ => false
            };
            if (shouldFail && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
                throw new InvalidOperationException($"Intentional repair interruption: {point}");
            return ValueTask.FromResult(result);
        }
    }

    private sealed record RepairProblem(string Code);

    private sealed record RepairResult(OrderBody Order, int ReservationsReleased, int TasksCancelled,
        int AllocationsRaised, int TasksRaised, decimal ShortfallQuantity, bool WasAlreadyRepaired);
}
