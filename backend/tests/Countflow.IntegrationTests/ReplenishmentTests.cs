using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Replenishment.Domain;
using Countflow.Modules.Replenishment.Features;
using Countflow.Modules.Replenishment.Persistence;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class ReplenishmentTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    private static readonly PostingActor Actor = new(Guid.Empty, "replenishment-test");

    [Fact]
    public async Task Reserved_replenishment_posts_its_own_movement_and_replays_after_a_lost_acknowledgement()
    {
        var stock = await StockAsync();
        Guid reservationId;
        using (var scope = fixture.Services.CreateScope())
        {
            var reservation = await scope.ServiceProvider.GetRequiredService<IStockReservations>().ReserveAsync(
                new ReservationInstruction { BalanceId = stock.BalanceId, Quantity = 10, OwnerId = Guid.CreateVersion7(),
                    OwnerReference = "replenishment-test", IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None);
            Assert.True(reservation.IsSuccess);
            reservationId = reservation.Value.Id;
        }
        var instruction = new PickInstruction { ReservationId = reservationId, ToLocationId = stock.FaceId, TaskId = Guid.CreateVersion7(),
            IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor, DecisionTraceId = Guid.CreateVersion7() };
        Guid movementId;
        using (var scope = fixture.Services.CreateScope())
        {
            var posted = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(instruction, CancellationToken.None);
            Assert.True(posted.IsSuccess);
            movementId = posted.Value.MovementId;
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var retry = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(instruction, CancellationToken.None);
            Assert.True(retry.IsSuccess);
            Assert.True(retry.Value.WasAlreadyPosted);
            Assert.Equal(movementId, retry.Value.MovementId);
            var duplicate = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(
                instruction with { IdempotencyKey = Guid.CreateVersion7().ToString() }, CancellationToken.None);
            Assert.True(duplicate.IsFailure);
        }
        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var movement = await db.StockMovements.SingleAsync(row => row.Id == movementId);
        Assert.Equal(MovementType.Replenishment, movement.Type);
        Assert.Equal(MovementReason.ReplenishmentPull, movement.Reason);
        var reservationState = await db.StockReservations.SingleAsync(row => row.Id == reservationId);
        Assert.Equal(ReservationState.Picked, reservationState.State);
        Assert.Equal(movementId, reservationState.ConsumedByMovementId);
        Assert.Equal(20, await db.StockBalances.Where(row => row.Id == stock.BalanceId).Select(row => row.Quantity).SingleAsync());
        Assert.Equal(10, await db.StockBalances.Where(row => row.ItemId == stock.ItemId && row.LocationId == stock.FaceId)
            .Select(row => row.Quantity).SingleAsync());
    }

    [Fact]
    public async Task A_replenishment_key_cannot_replay_a_different_reservation()
    {
        var stock = await StockAsync();
        async Task<Guid> Reserve()
        {
            using var scope = fixture.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IStockReservations>().ReserveAsync(
                new ReservationInstruction { BalanceId = stock.BalanceId, Quantity = 5, OwnerId = Guid.CreateVersion7(),
                    OwnerReference = "replenishment-test", IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None);
            Assert.True(result.IsSuccess);
            return result.Value.Id;
        }
        var first = await Reserve();
        var second = await Reserve();
        var instruction = new PickInstruction { ReservationId = first, ToLocationId = stock.FaceId,
            TaskId = Guid.CreateVersion7(), DecisionTraceId = Guid.CreateVersion7(), IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor };
        using (var scope = fixture.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(instruction, CancellationToken.None)).IsSuccess);
        using var retry = fixture.Services.CreateScope();
        var result = await retry.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(
            instruction with { ReservationId = second }, CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal("inventory.idempotency_key_reused", result.Error.Code);
    }

    [Fact]
    public async Task A_breached_face_raises_reserved_work_once_and_counts_incoming_stock()
    {
        var stock = await StockAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
            db.PickFacePolicies.Add(new PickFacePolicy { LocationId = stock.FaceId, WarehouseId = stock.WarehouseId,
                ItemId = stock.ItemId, Minimum = 10, Maximum = 50, Enabled = true });
            await db.SaveChangesAsync();
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        using var check = fixture.Services.CreateScope();
        var plans = await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.Include(plan => plan.Lines)
            .Where(plan => plan.LocationId == stock.FaceId).ToListAsync();
        var plan = Assert.Single(plans);
        Assert.Equal(20, (await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().PickFacePolicies.SingleAsync(policy => policy.LocationId == stock.FaceId)).LastShortfall);
        Assert.Equal(50, plan.RequestedQuantity);
        var line = Assert.Single(plan.Lines);
        Assert.Equal(30, line.Quantity);
        Assert.NotNull(line.ReservationId);
        Assert.NotNull(line.WorkTaskId);
        var held = await check.ServiceProvider.GetRequiredService<IStockAvailability>().HoldingsAsync(stock.WarehouseId, stock.ItemId, CancellationToken.None);
        Assert.Equal(0, Assert.Single(held, holding => holding.LocationId == stock.BulkId).AvailableQuantity);
        Assert.NotEqual(Guid.Empty, plan.DecisionTraceId);
    }

    [Fact]
    public async Task Replenishment_work_is_claimed_confirmed_and_reconciled_without_an_extra_top_up()
    {
        var stock = await StockAsync();
        using var supervisor = await ClientAsync(Roles.Supervisor);
        using var operatorClient = await ClientAsync(Roles.Operator);
        string faceCode;
        using (var scope = fixture.Services.CreateScope())
            faceCode = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations
                .Where(location => location.Id == stock.FaceId).Select(location => location.Code).SingleAsync();
        string sku;
        using (var scope = fixture.Services.CreateScope())
            sku = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Items.Where(item => item.Id == stock.ItemId)
                .Select(item => item.Sku).SingleAsync();
        var refused = await operatorClient.PutAsJsonAsync($"/api/v1/replenishment/pick-faces/{faceCode}", new SavePickFaceRequest(sku, 10, 50, true));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var saved = await supervisor.PutAsJsonAsync($"/api/v1/replenishment/pick-faces/{faceCode}", new SavePickFaceRequest(sku, 10, 50, true));
        saved.EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        Guid taskId;
        using (var scope = fixture.Services.CreateScope())
            taskId = (await scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.Include(plan => plan.Lines)
                .SingleAsync(plan => plan.LocationId == stock.FaceId)).Lines.Single().WorkTaskId!.Value;
        (await operatorClient.PostAsJsonAsync($"/api/v1/work/tasks/{taskId}/claim", new { })).EnsureSuccessStatusCode();
        using var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/work/tasks/{taskId}/complete")
            { Content = JsonContent.Create(new { toLocationCode = faceCode }) };
        complete.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        (await operatorClient.SendAsync(complete)).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        using var check = fixture.Services.CreateScope();
        var plan = await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.SingleAsync(plan => plan.LocationId == stock.FaceId);
        Assert.Equal(ReplenishmentPlanStatus.Finished, plan.Status);
        var holdings = await check.ServiceProvider.GetRequiredService<IStockAvailability>().HoldingsAsync(stock.WarehouseId, stock.ItemId, CancellationToken.None);
        Assert.Equal(30, Assert.Single(holdings).OnHandQuantity);
        Assert.Equal(stock.FaceId, Assert.Single(holdings).LocationId);
        var report = await check.ServiceProvider.GetRequiredService<Countflow.Modules.Inventory.Ledger.LedgerReconciler>().RunAsync(CancellationToken.None);
        Assert.True(report.IsClean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maximum_cannot_be_reduced_below_committed_incoming_stock(bool assigned)
    {
        var stock = await StockAsync();
        using var supervisor = await ClientAsync(Roles.Supervisor);
        using var operatorClient = await ClientAsync(Roles.Operator);
        string faceCode, sku;
        using (var scope = fixture.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            faceCode = (await master.Locations.FindAsync(stock.FaceId))!.Code;
            sku = (await master.Items.FindAsync(stock.ItemId))!.Sku;
        }
        (await supervisor.PutAsJsonAsync($"/api/v1/replenishment/pick-faces/{faceCode}", new SavePickFaceRequest(sku, 10, 50, true))).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
            if (assigned)
            {
                var plan = await scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.Include(plan => plan.Lines).SingleAsync(row => row.LocationId == stock.FaceId);
                (await operatorClient.PostAsJsonAsync($"/api/v1/work/tasks/{plan.Lines.Single().WorkTaskId}/claim", new { })).EnsureSuccessStatusCode();
            }
        }
        var refused = await supervisor.PutAsJsonAsync($"/api/v1/replenishment/pick-faces/{faceCode}", new SavePickFaceRequest(sku, 10, 20, true));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var check = fixture.Services.CreateScope();
        Assert.Equal(50, (await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().PickFacePolicies.FindAsync(stock.FaceId))!.Maximum);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(10, 10)]
    [InlineData(50, 10)]
    public async Task Invalid_min_max_settings_are_refused(decimal minimum, decimal maximum)
    {
        var stock = await StockAsync();
        using var supervisor = await ClientAsync(Roles.Supervisor);
        using var scope = fixture.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var faceCode = await master.Locations.Where(location => location.Id == stock.FaceId).Select(location => location.Code).SingleAsync();
        var sku = await master.Items.Where(item => item.Id == stock.ItemId).Select(item => item.Sku).SingleAsync();
        var response = await supervisor.PutAsJsonAsync($"/api/v1/replenishment/pick-faces/{faceCode}", new SavePickFaceRequest(sku, minimum, maximum, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().PickFacePolicies.AnyAsync(policy => policy.LocationId == stock.FaceId));
    }

    [Fact]
    public async Task Simultaneous_faces_cannot_commit_the_same_bulk_stock_twice()
    {
        var stock = await StockAsync();
        var other = await FaceAsync(stock.WarehouseId, stock.FaceId);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
            foreach (var faceId in new[] { stock.FaceId, other })
                db.PickFacePolicies.Add(new PickFacePolicy { LocationId = faceId, WarehouseId = stock.WarehouseId,
                    ItemId = stock.ItemId, Minimum = 10, Maximum = 50, Enabled = true });
            await db.SaveChangesAsync();
        }
        async Task Run(Guid faceId)
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{faceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        await Task.WhenAll(Run(stock.FaceId), Run(other));
        using var check = fixture.Services.CreateScope();
        var lines = await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Lines
            .Where(line => line.BalanceId == stock.BalanceId && line.WorkTaskId != null).ToListAsync();
        Assert.Equal(30, lines.Sum(line => line.Quantity));
        var reservations = await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations
            .Where(reservation => reservation.BalanceId == stock.BalanceId && reservation.State == ReservationState.Open)
            .SumAsync(reservation => reservation.Quantity);
        Assert.Equal(30, reservations);
    }

    [Fact]
    public async Task A_claimed_plan_refuses_withdrawal_and_hand_back_keeps_stock_reserved_until_cancellation()
    {
        var stock = await StockAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
            db.PickFacePolicies.Add(new PickFacePolicy { LocationId = stock.FaceId, WarehouseId = stock.WarehouseId,
                ItemId = stock.ItemId, Minimum = 10, Maximum = 50, Enabled = true });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        Guid planId, taskId, reservationId;
        using (var scope = fixture.Services.CreateScope())
        {
            var plan = await scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.Include(plan => plan.Lines)
                .SingleAsync(plan => plan.LocationId == stock.FaceId);
            planId = plan.Id; taskId = plan.Lines.Single().WorkTaskId!.Value; reservationId = plan.Lines.Single().ReservationId!.Value;
        }
        using var supervisor = await ClientAsync(Roles.Supervisor);
        using var operatorClient = await ClientAsync(Roles.Operator);
        (await operatorClient.PostAsJsonAsync($"/api/v1/work/tasks/{taskId}/claim", new { })).EnsureSuccessStatusCode();
        var refused = await supervisor.PostAsJsonAsync($"/api/v1/replenishment/plans/{planId}/cancel", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        (await operatorClient.PostAsJsonAsync($"/api/v1/work/tasks/{taskId}/release", new { })).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(ReservationState.Open, (await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations.FindAsync(reservationId))!.State);
        (await supervisor.PostAsJsonAsync($"/api/v1/replenishment/plans/{planId}/cancel", new { })).EnsureSuccessStatusCode();
        (await supervisor.PostAsJsonAsync($"/api/v1/replenishment/plans/{planId}/cancel", new { })).EnsureSuccessStatusCode();
        using var check = fixture.Services.CreateScope();
        Assert.Equal(ReservationState.Released, (await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations.FindAsync(reservationId))!.State);
        Assert.Equal(ReplenishmentPlanStatus.Cancelled, (await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.FindAsync(planId))!.Status);
    }

    [Fact]
    public async Task Allocated_outbound_stock_does_not_trigger_classic_min_max_replenishment()
    {
        var stock = await StockAsync();
        using var scope = fixture.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
        { ItemId = stock.ItemId, ToLocationId = stock.FaceId, Quantity = 30, IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None)).IsSuccess);
        var faceBalance = await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().StockBalances.SingleAsync(balance => balance.ItemId == stock.ItemId && balance.LocationId == stock.FaceId);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IStockReservations>().ReserveAsync(new ReservationInstruction
        { BalanceId = faceBalance.Id, Quantity = 20, OwnerId = Guid.CreateVersion7(), OwnerReference = "outbound", IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None)).IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
        db.PickFacePolicies.Add(new PickFacePolicy { LocationId = stock.FaceId, WarehouseId = stock.WarehouseId,
            ItemId = stock.ItemId, Minimum = 20, Maximum = 50, Enabled = true });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
            new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.False(await db.Plans.AnyAsync(plan => plan.LocationId == stock.FaceId));
    }

    [Fact]
    public async Task Replenishment_excludes_forward_stock_in_its_source_trace()
    {
        var stock = await StockAsync();
        using var scope = fixture.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
        { ItemId = stock.ItemId, ToLocationId = stock.FaceId, Quantity = 5, IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None)).IsSuccess);
        var advice = await scope.ServiceProvider.GetRequiredService<IAllocationAdvisor>().AllocateAsync(new AllocationQuery
        { WarehouseId = stock.WarehouseId, ItemId = stock.ItemId, Quantity = 20, RequiredSourceRole = StorageRole.Reserve, Actor = Actor }, CancellationToken.None);
        Assert.True(advice.IsSuccess);
        Assert.All(advice.Value.Lines, line => Assert.Equal(stock.BulkId, line.LocationId));
        var trace = await scope.ServiceProvider.GetRequiredService<IDecisionTraceReader>().FindAsync(advice.Value.DecisionTraceId, CancellationToken.None);
        Assert.Contains(trace!.Candidates, candidate => candidate.ExcludedBy?.Code == "rules.allocation.source_role_required");
    }

    [Fact]
    public async Task Replenishment_cannot_move_a_reservation_to_another_warehouse()
    {
        var stock = await StockAsync();
        Guid otherWarehouse;
        using (var scope = fixture.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var warehouse = new Warehouse { Code = $"RPL-{Guid.CreateVersion7():N}"[..16], Name = "Other warehouse" };
            master.Warehouses.Add(warehouse); await master.SaveChangesAsync(); otherWarehouse = warehouse.Id;
        }
        var faceId = await FaceAsync(otherWarehouse, null);
        using var attempt = fixture.Services.CreateScope();
        var held = await attempt.ServiceProvider.GetRequiredService<IStockReservations>().ReserveAsync(new ReservationInstruction
        { BalanceId = stock.BalanceId, Quantity = 5, OwnerId = Guid.CreateVersion7(), OwnerReference = "cross-warehouse",
            IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None);
        Assert.True(held.IsSuccess);
        var posted = await attempt.ServiceProvider.GetRequiredService<IStockPosting>().ReplenishAsync(new PickInstruction
        { ReservationId = held.Value.Id, ToLocationId = faceId, TaskId = Guid.CreateVersion7(), DecisionTraceId = Guid.CreateVersion7(),
            IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None);
        Assert.True(posted.IsFailure);
        Assert.Equal("inventory.replenishment_locations_invalid", posted.Error.Code);
    }

    [Fact]
    public async Task Lost_reservation_and_task_acknowledgements_resume_the_same_intent()
    {
        var stock = await StockAsync();
        Guid planId, reservationId, taskId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
            db.PickFacePolicies.Add(new PickFacePolicy { LocationId = stock.FaceId, WarehouseId = stock.WarehouseId,
                ItemId = stock.ItemId, Minimum = 10, Maximum = 50, Enabled = true });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
            var plan = await db.Plans.Include(plan => plan.Lines).SingleAsync(plan => plan.LocationId == stock.FaceId);
            planId = plan.Id; reservationId = plan.Lines.Single().ReservationId!.Value; taskId = plan.Lines.Single().WorkTaskId!.Value;
            plan.Lines.Single().ReservationId = null;
            plan.Lines.Single().WorkTaskId = null;
            var policy = await db.PickFacePolicies.FindAsync(stock.FaceId);
            policy!.PendingPlanId = plan.Id;
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        using var check = fixture.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<ReplenishmentDbContext>().Plans.Include(plan => plan.Lines).SingleAsync(plan => plan.Id == planId);
        Assert.Equal(reservationId, saved.Lines.Single().ReservationId);
        Assert.Equal(taskId, saved.Lines.Single().WorkTaskId);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockReservations.CountAsync(reservation => reservation.OwnerId == planId));
    }

    [Fact]
    public async Task A_completion_retry_finishes_work_after_inventory_committed_but_the_task_save_failed()
    {
        var stock = await StockAsync();
        Guid taskId;
        string faceCode;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReplenishmentDbContext>();
            db.PickFacePolicies.Add(new PickFacePolicy { LocationId = stock.FaceId, WarehouseId = stock.WarehouseId,
                ItemId = stock.ItemId, Minimum = 10, Maximum = 50, Enabled = true });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ReplenishmentRunner>().ExecuteAsync(
                new BackgroundJobRun($"replenishment.{stock.FaceId:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
            taskId = (await db.Plans.Include(plan => plan.Lines).SingleAsync(plan => plan.LocationId == stock.FaceId)).Lines.Single().WorkTaskId!.Value;
            faceCode = (await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations.FindAsync(stock.FaceId))!.Code;
        }
        using var operatorClient = await ClientAsync(Roles.Operator);
        (await operatorClient.PostAsJsonAsync($"/api/v1/work/tasks/{taskId}/claim", new { })).EnsureSuccessStatusCode();
        var interceptor = new FailCompletionSave();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<Countflow.Modules.Work.Persistence.WorkDbContext>(options => options.AddInterceptors(interceptor))));
        using var interrupted = host.CreateClient();
        interrupted.DefaultRequestHeaders.Authorization = operatorClient.DefaultRequestHeaders.Authorization;
        var key = Guid.CreateVersion7().ToString();
        async Task<HttpResponseMessage> Complete(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/work/tasks/{taskId}/complete")
                { Content = JsonContent.Create(new { toLocationCode = faceCode }) };
            request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.InternalServerError, (await Complete(interrupted)).StatusCode);
        (await Complete(operatorClient)).EnsureSuccessStatusCode();
        using var check = fixture.Services.CreateScope();
        var work = await check.ServiceProvider.GetRequiredService<Countflow.Modules.Work.Persistence.WorkDbContext>().WorkTasks.FindAsync(taskId);
        Assert.Equal(Countflow.Modules.Work.Domain.WorkTaskStatus.Completed, work!.Status);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<InventoryDbContext>().StockMovements.CountAsync(movement => movement.TaskId == taskId));
    }

    private sealed class FailCompletionSave : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Countflow.Modules.Work.Domain.WorkTask>()
                .Any(entry => entry.Entity.Status == Countflow.Modules.Work.Domain.WorkTaskStatus.Completed)
                && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
                throw new InvalidOperationException("Intentional failure after stock posting");
            return ValueTask.FromResult(result);
        }
    }

    private async Task<Guid> FaceAsync(Guid warehouseId, Guid? copyZoneFrom)
    {
        using var scope = fixture.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var zoneId = copyZoneFrom is { } id ? (await master.Locations.FindAsync(id))!.ZoneId : null;
        var face = new Location { WarehouseId = warehouseId, ZoneId = zoneId, Code = $"RPLFACE-{Guid.CreateVersion7():N}",
            Type = LocationType.PickFace, IsActive = true, IsPickable = true, IsReplenishable = true, MaxDistinctItems = 1 };
        master.Locations.Add(face); await master.SaveChangesAsync(); return face.Id;
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        const string secret = "Countflow#2026";
        var name = $"replenishment-{role.ToLowerInvariant()}";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
            if (await users.FindByNameAsync(name) is null)
            {
                var user = new CountflowUser { UserName = name, Email = $"{name}@countflow.test", EmailConfirmed = true, DisplayName = name, OperatorCode = $"RPL-{role}" };
                var created = await users.CreateAsync(user, secret);
                Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(name, secret));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<Session>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }
    private static object Credentials(string identifier, string secret) => new { identifier, password = secret };
    private sealed record Session(string AccessToken);

    private async Task<Stock> StockAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        if (!await master.Warehouses.AnyAsync())
        {
            var layout = new WarehouseLayoutBuilder().Build();
            master.Warehouses.Add(layout.Warehouse);
            master.Areas.AddRange(layout.Areas);
            master.Zones.AddRange(layout.Zones);
            master.Locations.AddRange(layout.Locations);
        }
        var item = new Item { Sku = $"RPL-{Guid.CreateVersion7():N}", Description = "Replenishment test", BaseUom = "EA" };
        item.PackConfigurations.Add(new PackConfiguration { ItemId = item.Id, Level = PackagingLevel.Each, BaseUnitQuantity = 1, IsPickUnit = true });
        master.Items.Add(item);
        await master.SaveChangesAsync();
        var bulk = await master.Locations.Where(location => location.Type == LocationType.Bulk).OrderBy(location => location.Code).FirstAsync();
        var faceIndex = await master.Items.CountAsync(row => row.Sku.StartsWith("RPL-")) - 1;
        var face = await master.Locations.Where(location => location.Type == LocationType.PickFace && location.IsReplenishable && location.Code.StartsWith("WH1-"))
            .OrderBy(location => location.Code).Skip(faceIndex).FirstAsync();
        var receipt = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(
            new ReceiptInstruction { ItemId = item.Id, Quantity = 30, ToLocationId = bulk.Id,
                IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = Actor }, CancellationToken.None);
        Assert.True(receipt.IsSuccess);
        var balance = await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().StockBalances
            .SingleAsync(row => row.ItemId == item.Id && row.LocationId == bulk.Id);
        return new Stock(item.Id, bulk.WarehouseId, bulk.Id, face.Id, balance.Id);
    }

    private sealed record Stock(Guid ItemId, Guid WarehouseId, Guid BulkId, Guid FaceId, Guid BalanceId);
}
