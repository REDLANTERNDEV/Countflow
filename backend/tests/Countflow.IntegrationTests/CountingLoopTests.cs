using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Security;
using Countflow.SharedKernel.Contracts;
using Countflow.Modules.Counting.Features.Schedules;
using Countflow.Modules.Counting.Features.Plans;
using Countflow.Modules.Counting.Domain;
using Countflow.Modules.Jobs;
using Countflow.SharedKernel.Modules;
using Countflow.Modules.Counting.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Phase 5 end to end: a count is raised by policy, an operator counts it on the
/// handheld, a variance posts a <c>CountAdjustment</c> carrying its decision
/// trace, and the console can report accuracy from what the counts found.
/// <para>
/// The first test proves that sentence. Everything after it defends the
/// properties that make it worth anything: that a count which agrees with the
/// book is recorded even though it posts nothing, that finding <i>more</i> is an
/// ordinary result rather than an error to refuse, that a retried confirmation
/// counts once, and that the policy will not send anybody to a bay that is being
/// worked or was checked on Tuesday.
/// </para>
/// <para>
/// <b>The quiet-period constraint is what makes these tests deterministic.</b>
/// Every holding in this database was created seconds ago, so it fails
/// <c>count.settled_stock</c> and cannot be picked up by a plan. Only the
/// holdings a test explicitly backdates with <see cref="SettleAsync"/> are
/// candidates, which is how a test that raises a plan knows what it will get
/// without the other tests' stock drifting into the answer.
/// </para>
/// </summary>
public sealed class CountingLoopTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private string _warehouseCode = string.Empty;
    private string _dock = string.Empty;
    private int _counter;

    public async Task InitializeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

            if (!await masterData.Warehouses.AnyAsync())
            {
                var layout = new WarehouseLayoutBuilder().Build();
                masterData.Warehouses.Add(layout.Warehouse);
                masterData.Areas.AddRange(layout.Areas);
                masterData.Zones.AddRange(layout.Zones);
                masterData.Locations.AddRange(layout.Locations);
                await masterData.SaveChangesAsync();
            }

            _warehouseCode = await masterData.Warehouses.Select(w => w.Code).FirstAsync();

            _dock = await masterData.Locations
                .Where(l => l.Type == LocationType.ReceivingDock)
                .OrderBy(l => l.Code)
                .Select(l => l.Code)
                .FirstAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("count-supervisor");
        _operator = await SignInAsync("count-operator");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_scheduled_run_uses_the_existing_policy_and_retries_without_duplicate_tasks()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);
        Guid warehouseId;
        var runId = Guid.CreateVersion7();
        var scheduledAt = DateTimeOffset.UtcNow;
        using (var scope = fixture.Services.CreateScope())
        {
            warehouseId = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Warehouses
                .Where(warehouse => warehouse.Code == _warehouseCode).Select(warehouse => warehouse.Id).SingleAsync();
            var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
            var schedule = await db.CountSchedules.FindAsync(warehouseId);
            if (schedule is null) { schedule = new CountSchedule { WarehouseId = warehouseId }; db.CountSchedules.Add(schedule); }
            schedule.Enabled = true;
            schedule.MaxTasks = 2;
            schedule.PendingRunId = runId;
            schedule.PendingScheduledAt = scheduledAt;
            schedule.NextRunAt = scheduledAt;
            await db.SaveChangesAsync();
        }
        var run = new BackgroundJobRun($"counting.{warehouseId:N}", runId, scheduledAt);
        var key = $"schedule:{warehouseId:N}:{runId:N}";
        List<Guid?>? originalTasks = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ScheduledCountPlanning>().ExecuteAsync(run, CancellationToken.None);
            if (attempt == 0)
            {
                var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
                var saved = await db.CountPlans.Include(plan => plan.Lines)
                    .SingleAsync(plan => plan.IdempotencyKey == key);
                originalTasks = saved.Lines.OrderBy(line => line.Rank).Select(line => line.WorkTaskId).ToList();
                // Simulate an enqueue whose acknowledgement was lost before the
                // task ID was saved on its plan line.
                saved.Lines.First().WorkTaskId = null;
                var schedule = await db.CountSchedules.FindAsync(warehouseId);
                schedule!.PendingRunId = run.Id;
                schedule.PendingScheduledAt = scheduledAt;
                await db.SaveChangesAsync();
            }
        }
        using var check = fixture.Services.CreateScope();
        var plan = await check.ServiceProvider.GetRequiredService<CountingDbContext>().CountPlans
            .Include(plan => plan.Lines).SingleOrDefaultAsync(plan => plan.IdempotencyKey == key);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.RequestedTasks);
        Assert.Equal("count-scheduler", plan.CreatedByUserName);
        Assert.NotEqual(Guid.Empty, plan.DecisionTraceId);
        Assert.Contains(plan.Lines, line => line.WorkTaskId is not null);
        Assert.Equal(plan.Lines.Count, plan.Lines.Select(line => line.WorkTaskId).Distinct().Count());
        Assert.Equal(originalTasks, plan.Lines.OrderBy(line => line.Rank).Select(line => line.WorkTaskId).ToList());
    }

    [Fact]
    public async Task Simultaneous_manual_and_scheduled_planning_exclude_each_others_open_holdings()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);
        Guid warehouseId;
        using (var scope = fixture.Services.CreateScope())
            warehouseId = await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Warehouses
                .Where(warehouse => warehouse.Code == _warehouseCode).Select(warehouse => warehouse.Id).SingleAsync();
        async Task<Countflow.SharedKernel.Results.Result<Countflow.Modules.Counting.Domain.CountPlan>> Plan(string key)
        {
            using var scope = fixture.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CountPlanning>().PlanAsync(
                warehouseId, 20, null, key, new PostingActor(Guid.Empty, "count-scheduler"), CancellationToken.None);
        }
        var plans = await Task.WhenAll(Plan(Guid.CreateVersion7().ToString()), Plan(Guid.CreateVersion7().ToString()));
        Assert.All(plans, plan => Assert.True(plan.IsSuccess));
        Assert.NotEqual(plans[0].Value.Reference, plans[1].Value.Reference);
        Assert.Empty(plans[0].Value.Lines.Select(line => line.BalanceId)
            .Intersect(plans[1].Value.Lines.Select(line => line.BalanceId)));
    }

    [Fact]
    public async Task Cancellation_after_lock_loss_prevents_stale_enqueues_from_creating_live_work()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);
        var gate = new EnqueueGate();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IWorkQueue>(provider => new BlockingCountQueue(
                ActivatorUtilities.CreateInstance<Countflow.Modules.Work.Integration.WorkQueue>(provider), gate))));
        var key = Guid.CreateVersion7().ToString();
        using var planningScope = host.Services.CreateScope();
        var master = planningScope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var warehouseId = await master.Warehouses.Where(warehouse => warehouse.Code == _warehouseCode)
            .Select(warehouse => warehouse.Id).SingleAsync();
        var planning = planningScope.ServiceProvider.GetRequiredService<CountPlanning>().PlanAsync(
            warehouseId, 20, null, key, new PostingActor(Guid.Empty, "count-scheduler"), CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Guid planId;
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
                planId = await db.CountPlans.Where(plan => plan.IdempotencyKey == key).Select(plan => plan.Id).SingleAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {$"countflow.count-planning:{warehouseId:N}"}");
            }
            var cancelled = await PostAsync(_supervisor, $"/api/v1/counting/plans/{planId}/cancel", new { });
            cancelled.EnsureSuccessStatusCode();
            gate.Resume.TrySetResult();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => planning);
            using var check = fixture.Services.CreateScope();
            var saved = await check.ServiceProvider.GetRequiredService<CountingDbContext>().CountPlans
                .Include(plan => plan.Lines).SingleAsync(plan => plan.Id == planId);
            Assert.Equal(CountPlanStatus.Cancelled, saved.Status);
            var tasks = await check.ServiceProvider.GetRequiredService<IWorkQueue>()
                .FindManyAsync(saved.Lines.Select(line => line.WorkTaskId!.Value).ToList(), CancellationToken.None);
            Assert.NotEmpty(tasks);
            Assert.All(tasks.Values, task => Assert.Equal(WorkTaskState.Cancelled, task.State));
        }
        finally
        {
            gate.Resume.TrySetResult();
            try { await planning; } catch (DbUpdateConcurrencyException) { }
        }
    }

    private sealed class EnqueueGate
    {
        public int Blocked;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingCountQueue(IWorkQueue inner, EnqueueGate gate) : IWorkQueue
    {
        public async Task<Countflow.SharedKernel.Results.Result<Guid>> EnqueueAsync(WorkTaskRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == WorkTaskKind.CycleCount && Interlocked.CompareExchange(ref gate.Blocked, 1, 0) == 0)
            {
                gate.Started.TrySetResult();
                await gate.Resume.Task.WaitAsync(cancellationToken);
            }
            return await inner.EnqueueAsync(request, cancellationToken);
        }
        public Task<IReadOnlyDictionary<Guid, WorkTaskSummary>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
            => inner.FindManyAsync(ids, cancellationToken);
        public Task<Countflow.SharedKernel.Results.Result<int>> CancelAsync(IReadOnlyCollection<Guid> ids, string reason, CancellationToken cancellationToken)
            => inner.CancelAsync(ids, reason, cancellationToken);
    }

    [Fact]
    public async Task A_planner_that_loses_its_lock_before_saving_cannot_duplicate_a_newer_intent()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);
        var gate = new EnqueueGate();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ICountPolicy>(provider => new BlockingCountPolicy(
                provider.GetRequiredService<Countflow.Modules.Rules.CycleCount.CountPolicyAdvisor>(), gate))));
        using var staleScope = host.Services.CreateScope();
        var warehouseId = await staleScope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Warehouses
            .Where(warehouse => warehouse.Code == _warehouseCode).Select(warehouse => warehouse.Id).SingleAsync();
        var staleKey = Guid.CreateVersion7().ToString();
        var stale = staleScope.ServiceProvider.GetRequiredService<CountPlanning>().PlanAsync(warehouseId, 20, null,
            staleKey, new PostingActor(Guid.Empty, "stale-planner"), CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var winnerScope = fixture.Services.CreateScope();
            var db = winnerScope.ServiceProvider.GetRequiredService<CountingDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {$"countflow.count-planning:{warehouseId:N}"}");
            var winner = await winnerScope.ServiceProvider.GetRequiredService<CountPlanning>().PlanAsync(warehouseId, 20,
                null, Guid.CreateVersion7().ToString(), new PostingActor(Guid.Empty, "winner"), CancellationToken.None);
            Assert.True(winner.IsSuccess);
            Assert.NotEmpty(winner.Value.Lines);
            gate.Resume.TrySetResult();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale);
            using var check = fixture.Services.CreateScope();
            Assert.False(await check.ServiceProvider.GetRequiredService<CountingDbContext>().CountPlans
                .AnyAsync(plan => plan.IdempotencyKey == staleKey));
        }
        finally
        {
            gate.Resume.TrySetResult();
            try { await stale; } catch (DbUpdateConcurrencyException) { }
        }
    }

    private sealed class BlockingCountPolicy(ICountPolicy inner, EnqueueGate gate) : ICountPolicy
    {
        public async Task<Countflow.SharedKernel.Results.Result<CountPlanAdvice>> AdviseAsync(CountPolicyQuery query, CancellationToken cancellationToken)
        {
            var result = await inner.AdviseAsync(query, cancellationToken);
            if (query.Actor.UserName == "stale-planner")
            {
                gate.Started.TrySetResult();
                await gate.Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task A_blocked_warehouse_does_not_delay_another_warehouses_next_schedule()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);
        var gate = new EnqueueGate();
        var clock = new CountClock();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IWorkQueue>(provider => new BlockingCountQueue(
                ActivatorUtilities.CreateInstance<Countflow.Modules.Work.Integration.WorkQueue>(provider), gate));
        }));
        Guid firstWarehouse;
        var other = new Warehouse { Code = $"SCH-{Guid.CreateVersion7():N}"[..16], Name = "Independent schedule" };
        using (var scope = host.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            master.Warehouses.Add(other);
            await master.SaveChangesAsync();
            firstWarehouse = await master.Warehouses.Where(warehouse => warehouse.Code == _warehouseCode)
                .Select(warehouse => warehouse.Id).SingleAsync();
            var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
            foreach (var id in new[] { firstWarehouse, other.Id })
            {
                var schedule = await db.CountSchedules.FindAsync(id);
                if (schedule is null) { schedule = new CountSchedule { WarehouseId = id }; db.CountSchedules.Add(schedule); }
                schedule.Enabled = true;
                schedule.IntervalMinutes = 1;
                schedule.NextRunAt = clock.GetUtcNow();
                schedule.PendingRunId = null;
                schedule.PendingScheduledAt = null;
            }
            await db.SaveChangesAsync();
        }
        var services = host.Services;
        using var worker = new BackgroundJobWorker(services.GetRequiredService<BackgroundJobRunner>(),
            Array.Empty<BackgroundJobRegistration>(), services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new JobRunnerOptions { PollInterval = TimeSpan.FromSeconds(1) }),
            clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundJobWorker>.Instance);
        async Task<Guid> WaitForPlan(Guid? previous)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                using var scope = services.CreateScope();
                var planId = await scope.ServiceProvider.GetRequiredService<CountingDbContext>().CountSchedules
                    .Where(schedule => schedule.WarehouseId == other.Id).Select(schedule => schedule.LastPlanId)
                    .SingleAsync(timeout.Token);
                if (planId is { } id && id != previous) return id;
                await Task.Delay(50, timeout.Token);
            }
        }
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var firstPlan = await WaitForPlan(null);
            clock.Advance(TimeSpan.FromMinutes(1));
            var secondPlan = await WaitForPlan(firstPlan);
            Assert.NotEqual(firstPlan, secondPlan);
            Assert.False(gate.Resume.Task.IsCompleted);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(shutdown.Token);
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
            var schedules = await db.CountSchedules.Where(schedule => schedule.WarehouseId == firstWarehouse || schedule.WarehouseId == other.Id).ToListAsync();
            foreach (var schedule in schedules) { schedule.Enabled = false; schedule.PendingRunId = null; schedule.PendingScheduledAt = null; }
            await db.SaveChangesAsync();
        }
    }

    private sealed class CountClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    [Theory]
    [InlineData(0, 1440, "counting.schedule_budget_invalid")]
    [InlineData(201, 1440, "counting.schedule_budget_invalid")]
    [InlineData(20, 0, "counting.schedule_interval_invalid")]
    [InlineData(20, 10081, "counting.schedule_interval_invalid")]
    public async Task Invalid_schedule_settings_are_refused(int budget, int interval, string code)
    {
        var warehouse = (await _supervisor.GetFromJsonAsync<List<CountScheduleResponse>>("/api/v1/counting/schedules"))!.First();
        var response = await _supervisor.PutAsJsonAsync($"/api/v1/counting/schedules/{warehouse.WarehouseId}",
            new SaveCountScheduleRequest(true, interval, budget));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ScheduleProblem>())!.Code);
    }

    [Fact]
    public async Task A_supervisor_can_enable_and_disable_a_schedule_but_an_operator_cannot()
    {
        var warehouse = (await _supervisor.GetFromJsonAsync<List<CountScheduleResponse>>("/api/v1/counting/schedules"))!.First();
        var path = $"/api/v1/counting/schedules/{warehouse.WarehouseId}";
        var refused = await _operator.PutAsJsonAsync(path, new SaveCountScheduleRequest(true, 60, 3));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var enabled = await _supervisor.PutAsJsonAsync(path, new SaveCountScheduleRequest(true, 60, 3));
        enabled.EnsureSuccessStatusCode();
        var saved = (await enabled.Content.ReadFromJsonAsync<CountScheduleResponse>())!;
        Assert.True(saved.Enabled);
        Assert.Equal(60, saved.IntervalMinutes);
        Assert.Equal(3, saved.MaxTasks);
        Assert.NotNull(saved.NextRunAt);
        var disabled = await _supervisor.PutAsJsonAsync(path, new SaveCountScheduleRequest(false, 60, 3));
        disabled.EnsureSuccessStatusCode();
        Assert.Null((await disabled.Content.ReadFromJsonAsync<CountScheduleResponse>())!.NextRunAt);
    }

    [Fact]
    public async Task A_schedule_for_an_unavailable_warehouse_retains_its_pending_identity()
    {
        var warehouseId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CountingDbContext>();
            db.CountSchedules.Add(new CountSchedule { WarehouseId = warehouseId, Enabled = true,
                PendingRunId = runId, PendingScheduledAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var attempt = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.ServiceProvider
            .GetRequiredService<CountScheduleRunner>().RunAsync(warehouseId, CancellationToken.None));
        using var check = fixture.Services.CreateScope();
        var state = await check.ServiceProvider.GetRequiredService<CountingDbContext>().CountSchedules.FindAsync(warehouseId);
        Assert.Equal(runId, state!.PendingRunId);
        Assert.Equal("counting.schedule_failed", state.LastErrorCode);
        state.Enabled = false;
        state.PendingRunId = null;
        state.PendingScheduledAt = null;
        await check.ServiceProvider.GetRequiredService<CountingDbContext>().SaveChangesAsync();
    }

    private sealed record ScheduleProblem(string Code);

    // --------------------------------------------------------------- the loop

    [Fact]
    public async Task A_count_is_raised_by_policy_counted_on_the_handheld_and_its_variance_posts_with_a_trace()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 100m);
        await SettleAsync(sku);

        // 1. The policy chooses, and says why. Nothing about this call names a bay.
        var plan = await RaisePlanAsync();

        var line = Assert.Single(plan.Lines, l => l.LocationCode == bin);
        Assert.Equal(sku, line.Sku);
        Assert.Equal(100m, line.ExpectedQuantity);
        Assert.NotNull(line.WorkTaskId);
        Assert.NotEqual(Guid.Empty, plan.DecisionTraceId);

        // 2. An ordinary job on the ordinary queue, and it explains itself the same
        //    way a putaway does. The bay is both where it starts and where it ends.
        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{line.WorkTaskId}/claim", new { }));

        Assert.Equal("CycleCount", claimed.Task.Kind);
        Assert.Equal(bin, claimed.Task.FromLocationCode);
        Assert.Equal(bin, claimed.Task.SuggestedLocationCode);
        Assert.NotNull(claimed.Decision);
        Assert.Equal("CycleCount", claimed.Decision!.Kind);

        // 3. The operator finds four fewer than the book says.
        var confirmed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 96m }));

        Assert.Equal("Completed", confirmed.Task.Status);
        Assert.Equal(96m, confirmed.Task.CountedQuantity);

        // A count has nothing to override: the operator reported a number rather
        // than choosing a destination, and the disagreement is the variance.
        Assert.False(confirmed.Task.WasOverridden);

        Assert.NotNull(confirmed.Count);
        Assert.Equal(100m, confirmed.Count!.ExpectedQuantity);
        Assert.Equal(96m, confirmed.Count.CountedQuantity);
        Assert.Equal(-4m, confirmed.Count.Variance);

        // 4. One movement, of the right type, carrying the policy's trace — the
        //    Phase 3 guarantee applied to a decision that is not about placement.
        var adjustment = Assert.Single(await MovementsOfTypeAsync(sku, "CountAdjustment"));
        Assert.Equal(4m, adjustment.Quantity);
        Assert.Equal(bin, adjustment.FromLocationCode);
        Assert.Equal(line.WorkTaskId, adjustment.TaskId);
        Assert.Equal(plan.DecisionTraceId, adjustment.DecisionTraceId);

        // 5. The shelf now reads what the operator counted, and the ledger still
        //    balances: the four units went to the Adjustment counterparty, they did
        //    not evaporate.
        Assert.Equal(96m, await BalanceAsync(sku, bin));
        Assert.Equal(0m, await EveryHoldingAsync(sku));

        // 6. And the console can report it.
        var accuracy = await GetAsync<AccuracyBody>($"/api/v1/inventory/accuracy?warehouseId={await WarehouseIdAsync()}");
        Assert.True(accuracy.Overall.Counted >= 1);
        Assert.Contains(accuracy.ByClass, slice => slice.Counted > 0);
    }

    // ------------------------------------------------------- the clean count

    [Fact]
    public async Task A_count_that_agrees_with_the_book_posts_nothing_and_is_still_recorded()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 40m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var confirmed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 40m }));

        // No ledger entry, and that empty list is the success. This is the whole
        // reason a count needed a record of its own: accuracy is verified over
        // counted, and a system that only wrote down the counts that went wrong
        // would know its numerator and have to guess at its denominator.
        Assert.Empty(confirmed.Movements);
        Assert.Empty(await MovementsOfTypeAsync(sku, "CountAdjustment"));

        Assert.NotNull(confirmed.Count);
        Assert.Equal(0m, confirmed.Count!.Variance);
        Assert.Null(confirmed.Count.Adjustment);

        Assert.Equal(40m, await BalanceAsync(sku, bin));

        // The count is nonetheless on the record, and the plan knows it happened.
        var plan = await GetAsync<CountPlanBody>($"/api/v1/counting/plans/{line.PlanId}");
        var stored = Assert.Single(plan.Lines, l => l.LocationCode == bin);
        Assert.Equal(40m, stored.CountedQuantity);
        Assert.Equal(0m, stored.Variance);
        Assert.Equal("Completed", stored.State);
    }

    // ------------------------------------------------------ finding more

    [Fact]
    public async Task Finding_more_than_expected_is_the_common_case_and_not_refused()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 12m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        // A mis-pick from three weeks ago, discovered. Nothing about this is an
        // error to be rejected — the operator is standing in front of the stock and
        // the system is not.
        var confirmed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 15m }));

        Assert.Equal(3m, confirmed.Count!.Variance);

        var adjustment = Assert.Single(await MovementsOfTypeAsync(sku, "CountAdjustment"));
        Assert.Equal(3m, adjustment.Quantity);

        // The Adjustment counterparty is the *source*, which is what makes the gain
        // visibly a gain rather than a quiet increment of a shelf.
        Assert.Equal(bin, adjustment.ToLocationCode);
        Assert.NotEqual(bin, adjustment.FromLocationCode);

        Assert.Equal(15m, await BalanceAsync(sku, bin));
        Assert.Equal(0m, await EveryHoldingAsync(sku));
    }

    [Fact]
    public async Task A_count_of_an_empty_shelf_is_an_answer_rather_than_a_missing_one()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 6m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var confirmed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 0m }));

        Assert.Equal(-6m, confirmed.Count!.Variance);
        Assert.Equal(0m, await BalanceAsync(sku, bin));
        Assert.Equal(0m, await EveryHoldingAsync(sku));
    }

    /// <summary>
    /// The book moving between the plan and the count is a real movement, not a
    /// race — so the correction is the difference the operator observed, applied as
    /// a delta, and the pick that happened in between stays correctly in the ledger
    /// on its own account.
    /// </summary>
    [Fact]
    public async Task A_movement_between_planning_and_counting_does_not_corrupt_the_variance()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 30m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);
        Assert.Equal(30m, line.ExpectedQuantity);

        // Ten units genuinely leave while the counter is walking over.
        await Read<object>(await PostAsync(
            _supervisor,
            "/api/v1/inventory/transfers",
            new { sku, quantity = 10m, fromLocationCode = bin, toLocationCode = _dock }));

        // The operator counts 22 — two more than the book now holds.
        var confirmed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 22m }));

        Assert.Equal(20m, confirmed.Count!.ExpectedQuantity);
        Assert.Equal(2m, confirmed.Count.Variance);

        Assert.Equal(22m, await BalanceAsync(sku, bin));
        Assert.Equal(0m, await EveryHoldingAsync(sku));
    }

    // -------------------------------------------------------- idempotency

    [Fact]
    public async Task A_retried_confirmation_counts_once_and_a_second_one_is_refused()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 50m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var key = Guid.CreateVersion7().ToString();
        var body = new { toLocationCode = bin, countedQuantity = 47m };

        var first = await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", body, key));

        // The handheld never heard the answer and asked again.
        var replay = await PostAsync(_operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", body, key);
        Assert.True(replay.IsSuccessStatusCode);

        // A *different* key against a completed job is a second confirmation, which
        // would adjust the shelf twice.
        var second = await PostAsync(_operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", body);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(-3m, first.Count!.Variance);
        Assert.Single(await MovementsOfTypeAsync(sku, "CountAdjustment"));
        Assert.Equal(47m, await BalanceAsync(sku, bin));

        // One count on the record, so the accuracy denominator is one.
        Assert.Equal(1, await CountsRecordedAsync(sku));
    }

    [Fact]
    public async Task A_retried_clean_count_is_recorded_once()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 25m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var key = Guid.CreateVersion7().ToString();
        var body = new { toLocationCode = bin, countedQuantity = 25m };

        await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", body, key));

        var replay = await PostAsync(_operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", body, key);
        Assert.True(replay.IsSuccessStatusCode);

        // Nothing posted either time, and the bay is counted once. A double-counted
        // clean count corrupts no stock figure at all and quietly doubles the
        // denominator of every accuracy number on the dashboard, which is worse
        // for being invisible.
        Assert.Empty(await MovementsOfTypeAsync(sku, "CountAdjustment"));
        Assert.Equal(1, await CountsRecordedAsync(sku));
    }

    // ------------------------------------------------------ what is refused

    [Fact]
    public async Task A_count_confirmed_without_a_quantity_is_refused()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 8m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var response = await PostAsync(
            _operator, $"/api/v1/work/tasks/{line.WorkTaskId}/complete", new { toLocationCode = bin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("count_quantity_required", await response.Content.ReadAsStringAsync());

        // Still open, so somebody can go back and do it properly.
        var task = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{line.WorkTaskId}");
        Assert.NotEqual("Completed", task.Task.Status);
    }

    [Fact]
    public async Task A_count_confirmed_at_a_different_bay_is_refused_rather_than_recorded_as_an_override()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 8m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        // Confirming a putaway somewhere else is a judgement worth capturing.
        // Confirming a count somewhere else would adjust one holding on the
        // strength of a look at another.
        var response = await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = _dock, countedQuantity = 8m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("count_location_mismatch", await response.Content.ReadAsStringAsync());

        Assert.Empty(await MovementsOfTypeAsync(sku, "CountAdjustment"));
    }

    [Fact]
    public async Task A_negative_count_is_refused_as_the_typing_slip_it_is()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 8m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        var response = await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = -1m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("counted_quantity_negative", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ the policy

    [Fact]
    public async Task Stock_that_has_just_been_worked_is_considered_and_excluded_by_name()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 60m);

        // Deliberately not settled: the putaway landed moments ago. Counting it now
        // would measure the truck, and a pallet part-way through being broken down
        // disagrees with the book at almost any instant.
        var plan = await RaisePlanAsync();

        Assert.DoesNotContain(plan.Lines, l => l.LocationCode == bin);

        // Considered and refused by a named rule rather than filtered out of a
        // query — the trace has to be able to say the bay was looked at (ADR 0004).
        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{plan.DecisionTraceId}");

        Assert.Contains(
            trace.Candidates,
            candidate => !candidate.IsAdmissible
                         && candidate.CandidateKey.StartsWith(bin, StringComparison.Ordinal)
                         && candidate.ExcludedBy!.Code == "rules.count.recently_moved");
    }

    [Fact]
    public async Task A_holding_already_on_somebody_s_list_is_not_raised_a_second_time()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 90m);
        await SettleAsync(sku);

        var first = await RaisePlanAsync();
        Assert.Contains(first.Lines, l => l.LocationCode == bin);

        // Without this the queue would fill with duplicates of exactly the holdings
        // that matter most: the worst ones come top of every ranking.
        var second = await RaisePlanAsync();
        Assert.DoesNotContain(second.Lines, l => l.LocationCode == bin);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{second.DecisionTraceId}");

        Assert.Contains(
            trace.Candidates,
            candidate => !candidate.IsAdmissible
                         && candidate.CandidateKey.StartsWith(bin, StringComparison.Ordinal)
                         && candidate.ExcludedBy!.Code == "rules.count.already_scheduled");
    }

    [Fact]
    public async Task A_bay_counted_this_week_is_left_alone()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 15m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 15m }));

        // The anti-churn rule. A holding that has just been counted has just been
        // written to, which makes it freshly interesting to half the preferences —
        // so without this the same bay would come round every planning run while
        // the far end of the building went unvisited for a year.
        await SettleAsync(sku);

        var again = await RaisePlanAsync();
        Assert.DoesNotContain(again.Lines, l => l.LocationCode == bin);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{again.DecisionTraceId}");

        Assert.Contains(
            trace.Candidates,
            candidate => !candidate.IsAdmissible
                         && candidate.CandidateKey.StartsWith(bin, StringComparison.Ordinal)
                         && candidate.ExcludedBy!.Code == "rules.count.counted_recently");
    }

    [Fact]
    public async Task A_never_counted_holding_outranks_one_checked_recently()
    {
        var stale = await NewItemAsync();
        var staleBin = await ReceiveAndPutAwayAsync(stale, 20m);
        await SettleAsync(stale);

        var plan = await RaisePlanAsync();
        var line = Assert.Single(plan.Lines, l => l.LocationCode == staleBin);

        // Never counted scores 1 outright on the overdue preference: there is no
        // fraction to take of an interval that never started, and a holding nobody
        // has ever verified is as overdue as a holding can be.
        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{plan.DecisionTraceId}");

        var evaluated = Assert.Single(
            trace.Candidates,
            candidate => candidate.CandidateKey.StartsWith(staleBin, StringComparison.Ordinal));

        var overdue = Assert.Single(evaluated.Scores, s => s.RuleCode == "count.overdue");
        Assert.Equal(1m, overdue.Value);
        Assert.Equal("rules.count.never_counted", overdue.Reason.Code);
        Assert.True(line.Score > 0m);
    }

    /// <summary>
    /// The regression guard for the flaw the Phase 5 container run found, and the
    /// third of its kind after Phase 4's two.
    /// <para>
    /// Travel is normalised against the whole warehouse, not against the admissible
    /// field. The container run showed what the difference costs: four holdings in
    /// two adjacent bays came back scored 0% and 100% along the route, so a few
    /// metres took the full width of the score and the weakest preference in the
    /// set silently outvoted the strongest.
    /// </para>
    /// <para>
    /// A lone candidate is what tells the two apart cleanly. Against the field it
    /// is degenerate — the nearest and the furthest thing there is — so the span is
    /// zero and it scores a flat 1, saying nothing about where it actually is.
    /// Against the building it scores its real position, which for a bay at the
    /// front is high but not perfect. Anything that reads exactly 1 here means the
    /// scale has been quietly changed back to the candidates.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Travel_is_measured_against_the_building_rather_than_the_candidate_field()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);
        await SettleAsync(sku);

        var plan = await RaisePlanAsync();
        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{plan.DecisionTraceId}");

        // Every other holding in this database is either unsettled or already on a
        // plan, so this is the only thing the policy can admit — which is the
        // degenerate field the assertion below turns on.
        var admissible = Assert.Single(trace.Candidates, c => c.IsAdmissible);
        Assert.StartsWith(bin, admissible.CandidateKey, StringComparison.Ordinal);

        var travel = Assert.Single(admissible.Scores, s => s.RuleCode == "count.travel_distance");

        Assert.True(
            travel.Value < 1m,
            $"The only candidate scored {travel.Value} on travel. A flat 1 means the normaliser " +
            "is measuring the candidate field, where a lone bay is both nearest and furthest, " +
            "rather than the warehouse.");

        // Still high: it is a bay at the front of a 2,245-location building, and
        // saying so is the point. Scoring it 0 would be the Phase 4 bug run the
        // other way, where a degenerate field was reported as maximally distant.
        Assert.True(travel.Value > 0.5m, $"Front-of-warehouse bay scored {travel.Value} on travel.");
    }

    [Fact]
    public async Task A_preview_answers_the_same_question_and_raises_nothing()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 33m);
        await SettleAsync(sku);

        var preview = await Read<PreviewCountBody>(await PostAsync(
            _supervisor,
            "/api/v1/rules/cycle-count/preview",
            new { warehouseCode = _warehouseCode, maxTasks = 50 }));

        Assert.Contains(preview.Lines, l => l.LocationCode == bin);
        Assert.NotEmpty(preview.Trace.Candidates);

        // Asking the question does not answer it differently next time: nothing was
        // raised, so the holding is still a candidate rather than "already
        // scheduled". That matters more here than for the other two previews.
        var plan = await RaisePlanAsync();
        Assert.Contains(plan.Lines, l => l.LocationCode == bin);
    }

    [Fact]
    public async Task Withdrawing_a_plan_takes_its_counts_back_out_of_the_queue()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 18m);
        await SettleAsync(sku);

        var plan = await RaisePlanAsync();
        var line = Assert.Single(plan.Lines, l => l.LocationCode == bin);

        var cancelled = await Read<CancelPlanBody>(await PostAsync(
            _supervisor, $"/api/v1/counting/plans/{plan.Id}/cancel", new { reason = "Shift ended" }));

        Assert.True(cancelled.WithdrawnCount >= 1);

        var task = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{line.WorkTaskId}");
        Assert.Equal("Cancelled", task.Task.Status);

        // The holding is a candidate again, because nobody is on their way to it.
        var again = await RaisePlanAsync();
        Assert.Contains(again.Lines, l => l.LocationCode == bin);
    }

    [Fact]
    public async Task A_plan_cannot_be_withdrawn_out_from_under_an_operator_holding_a_count()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 22m);
        await SettleAsync(sku);

        var plan = await RaisePlanAsync();
        var line = Assert.Single(plan.Lines, l => l.LocationCode == bin);

        await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{line.WorkTaskId}/claim", new { }));

        var response = await PostAsync(_supervisor, $"/api/v1/counting/plans/{plan.Id}/cancel", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("counts_in_progress", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_retried_planning_request_converges_on_the_plan_it_already_made()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 44m);
        await SettleAsync(sku);

        var key = Guid.CreateVersion7().ToString();

        var first = await Read<CountPlanBody>(await PostAsync(
            _supervisor,
            "/api/v1/counting/plans",
            new { warehouseCode = _warehouseCode, maxTasks = 50 },
            key));

        var again = await Read<CountPlanBody>(await PostAsync(
            _supervisor,
            "/api/v1/counting/plans",
            new { warehouseCode = _warehouseCode, maxTasks = 50 },
            key));

        // The same plan, not a second one. Running the policy again would see the
        // first run's jobs as already scheduled and quietly return a different set
        // of bays.
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Reference, again.Reference);
        Assert.Equal(first.DecisionTraceId, again.DecisionTraceId);

        var taskIds = first.Lines.Select(l => l.WorkTaskId).ToList();
        Assert.Equal(taskIds, again.Lines.Select(l => l.WorkTaskId).ToList());

        Assert.Contains(first.Lines, l => l.LocationCode == bin);
    }

    // ------------------------------------------------------------- the write-off

    [Fact]
    public async Task A_write_off_leaves_through_the_scrap_counterparty_and_the_ledger_still_balances()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 20m);

        var scrapped = await Read<MovementReceiptBody>(await PostAsync(
            _supervisor,
            "/api/v1/inventory/scrap",
            new
            {
                sku,
                quantity = 5m,
                locationCode = bin,
                cause = "Damaged",
                notes = "Crushed in the aisle; found during a count."
            }));

        Assert.Equal(5m, scrapped.Quantity);
        Assert.Equal(bin, scrapped.FromLocationCode);

        var movement = Assert.Single(await MovementsOfTypeAsync(sku, "Scrap"));
        Assert.Equal(5m, movement.Quantity);

        // Not netted against the Adjustment account: a write-off is a real physical
        // loss and a correction is a bookkeeping one, and a shrinkage report that
        // added them together would show whichever was larger.
        Assert.Empty(await MovementsOfTypeAsync(sku, "Adjustment"));

        Assert.Equal(15m, await BalanceAsync(sku, bin));
        Assert.Equal(0m, await EveryHoldingAsync(sku));
    }

    [Fact]
    public async Task A_write_off_with_no_stated_reason_is_refused()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 4m);

        var response = await PostAsync(
            _supervisor,
            "/api/v1/inventory/scrap",
            new { sku, quantity = 1m, locationCode = bin, cause = "Damaged", notes = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("scrap_notes_required", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ the dashboard

    [Fact]
    public async Task The_reconciler_s_findings_are_kept_and_read_back_rather_than_recomputed()
    {
        await Read<object>(await PostAsync(_supervisor, "/api/v1/inventory/reconciliation", new { }));

        var history = await GetAsync<List<ReconciliationRunBody>>("/api/v1/inventory/reconciliation/history");

        var run = Assert.Single(history, r => r.Trigger == "Manual");
        Assert.True(run.IsClean);
        Assert.Equal(0m, run.LedgerSum);
        Assert.Equal(0, run.DriftCount);
        Assert.True(run.HoldingsChecked >= 0);
    }

    [Fact]
    public async Task A_commitment_left_standing_against_stock_that_has_moved_shows_up_as_stranded()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 30m);

        var bench = await BenchAsync();

        var order = await Read<OrderBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewReference("SO"),
                stagingLocationCode = bench,
                lines = new[] { new { sku, quantity = 30m } }
            }));

        await Read<object>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { }));

        // An ad-hoc transfer moves the goods out from under the claim. The pick will
        // fail loudly when somebody tries it, which is the right failure — but
        // nobody should have to be the picker to find out.
        await Read<object>(await PostAsync(
            _supervisor,
            "/api/v1/inventory/transfers",
            new { sku, quantity = 30m, fromLocationCode = bin, toLocationCode = _dock }));

        var stranded = await GetAsync<List<StrandedBody>>("/api/v1/inventory/reservations/stranded");

        var row = Assert.Single(stranded, s => s.Sku == sku);
        Assert.Equal(bin, row.LocationCode);
        Assert.Equal(30m, row.ReservedQuantity);
        Assert.Equal(0m, row.OnHandQuantity);
        Assert.Equal(30m, row.Shortfall);
        Assert.Equal(order.Reference + "#1", row.OwnerReference);
    }

    [Fact]
    public async Task Override_rates_are_reported_by_kind_and_a_count_never_shows_one()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 11m);
        await SettleAsync(sku);

        var line = await RaiseAndFindAsync(bin);

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{line.WorkTaskId}/complete",
            new { toLocationCode = bin, countedQuantity = 11m }));

        var statistics = await GetAsync<WorkStatisticsBody>(
            $"/api/v1/work/statistics?warehouseId={await WarehouseIdAsync()}");

        var counts = Assert.Single(statistics.ByKind, k => k.Kind == "CycleCount");

        Assert.True(counts.Completed >= 1);

        // Structurally zero rather than suspiciously good: a count has no
        // destination to disagree about.
        Assert.Equal(0, counts.Overridden);
        Assert.Equal(0m, counts.OverrideRate);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Ages a SKU's holdings past the policy's quiet period.
    /// <para>
    /// The stock has not changed; the clock has. Backdating the projection's
    /// <c>UpdatedAt</c> is how a test says "this pallet has been standing there
    /// since yesterday" without a fake clock that every other module would then
    /// have to agree about — and it is the only lever these tests need, because
    /// every other holding in the database was written seconds ago and is therefore
    /// invisible to the policy.
    /// </para>
    /// </summary>
    private async Task SettleAsync(string sku)
    {
        using var scope = fixture.Services.CreateScope();

        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var itemId = await masterData.Items.Where(i => i.Sku == sku).Select(i => i.Id).SingleAsync();

        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await inventory.StockBalances
            .Where(b => b.ItemId == itemId)
            .ExecuteUpdateAsync(update =>
                update.SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
    }

    /// <summary>Raises a plan big enough to take everything the policy admits.</summary>
    private async Task<CountPlanBody> RaisePlanAsync() =>
        await Read<CountPlanBody>(await PostAsync(
            _supervisor,
            "/api/v1/counting/plans",
            new { warehouseCode = _warehouseCode, maxTasks = 50 }));

    /// <summary>Raises a plan and returns the line for one bay, with its plan id attached.</summary>
    private async Task<PlanLine> RaiseAndFindAsync(string bin)
    {
        var plan = await RaisePlanAsync();
        var line = Assert.Single(plan.Lines, l => l.LocationCode == bin);

        return line with { PlanId = plan.Id };
    }

    /// <summary>
    /// Puts stock on a shelf the way the warehouse actually does — a dock receipt,
    /// a rule-chosen destination and a confirmed putaway. Setting balances directly
    /// would be quicker and would test an arrangement of stock the system cannot
    /// reach.
    /// </summary>
    private async Task<string> ReceiveAndPutAwayAsync(string sku, decimal quantity)
    {
        var received = await Read<DockReceipt>(await PostAsync(
            _operator,
            "/api/v1/inbound/receipts",
            new { sku, quantity, toLocationCode = _dock }));

        Assert.NotNull(received.SuggestedLocationCode);
        Assert.NotNull(received.WorkTaskId);

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = received.SuggestedLocationCode }));

        return received.SuggestedLocationCode!;
    }

    private async Task<int> CountsRecordedAsync(string sku)
    {
        using var scope = fixture.Services.CreateScope();

        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var itemId = await masterData.Items.Where(i => i.Sku == sku).Select(i => i.Id).SingleAsync();

        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        return await inventory.StockCounts.CountAsync(c => c.ItemId == itemId);
    }

    private async Task<Guid> WarehouseIdAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        return await masterData.Warehouses
            .Where(w => w.Code == _warehouseCode)
            .Select(w => w.Id)
            .SingleAsync();
    }

    private async Task<string> BenchAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        return await masterData.Locations
            .Where(l => l.Type == LocationType.Packing)
            .OrderBy(l => l.Code)
            .Select(l => l.Code)
            .FirstAsync();
    }

    private string NewReference(string prefix, [CallerMemberName] string caller = "") =>
        $"{prefix}-{caller[..Math.Min(caller.Length, 28)]}-{Interlocked.Increment(ref _counter):D2}";

    private async Task<string> NewItemAsync(
        AbcClass band = AbcClass.B,
        [CallerMemberName] string caller = "")
    {
        var sku = $"CNT-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Counting test item for {caller}",
            BaseUom = "EA",
            AbcClass = band
        };

        item.PackConfigurations.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Each,
            BaseUnitQuantity = 1,
            LengthMm = 100m,
            WidthMm = 100m,
            HeightMm = 100m,
            GrossWeightKg = 0.5m,
            IsPickUnit = true
        });

        masterData.Items.Add(item);
        await masterData.SaveChangesAsync();

        return sku;
    }

    private async Task<decimal> BalanceAsync(string sku, string locationCode)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status=Available");

        return page.Items.Sum(r => r.Quantity);
    }

    /// <summary>
    /// Every holding of the SKU anywhere, physical and virtual, summed. Zero is the
    /// double-entry claim, and a count adjustment has to satisfy it exactly as a
    /// despatch does: the units a count "creates" came from the Adjustment account,
    /// which now owes them.
    /// </summary>
    private async Task<decimal> EveryHoldingAsync(string sku)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&includeEmpty=true&pageSize=500");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<List<MovementRow>> MovementsOfTypeAsync(string sku, string type)
    {
        var page = await GetAsync<MovementPage>(
            $"/api/v1/inventory/movements?sku={Uri.EscapeDataString(sku)}");

        return [.. page.Items.Where(m => m.Type == type)];
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString());

        return await client.SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _supervisor.GetAsync(url);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode} for {url}: " +
            $"{await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task EnsureUsersAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<CountflowRole>>();
        var users = services.GetRequiredService<UserManager<CountflowUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new CountflowRole(role));
            }
        }

        await EnsureAsync("count-supervisor", "CNT-SUP", Roles.Supervisor);
        await EnsureAsync("count-operator", "CNT-OP1", Roles.Operator);

        async Task EnsureAsync(string userName, string operatorCode, string role)
        {
            if (await users.FindByNameAsync(userName) is not null)
            {
                return;
            }

            var user = new CountflowUser
            {
                UserName = userName,
                Email = $"{userName}@countflow.test",
                EmailConfirmed = true,
                DisplayName = userName,
                OperatorCode = operatorCode
            };

            var created = await users.CreateAsync(user, Secret);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            await users.AddToRoleAsync(user, role);
        }
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(userName, Secret));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);

        return client;
    }

    // ----------------------------------------------------------- wire shapes

    private sealed record LoginResponse(string AccessToken);

    private sealed record DockReceipt(string Sku, string? SuggestedLocationCode, Guid? WorkTaskId);

    private sealed record PostedMovementBody(Guid MovementId, long Sequence, decimal Quantity);

    private sealed record CountOutcomeBody(
        Guid CountId,
        decimal ExpectedQuantity,
        decimal CountedQuantity,
        decimal Variance,
        PostedMovementBody? Adjustment);

    private sealed record TaskBody(
        Guid Id,
        string Kind,
        string Status,
        string Sku,
        decimal Quantity,
        string FromLocationCode,
        string? SuggestedLocationCode,
        Guid? DecisionTraceId,
        string? CompletedAtLocationCode,
        bool WasOverridden,
        decimal? CountedQuantity);

    private sealed record TaskDetail(TaskBody Task, TraceView? Decision);

    private sealed record TaskCompletion(
        TaskBody Task,
        List<PostedMovementBody> Movements,
        CountOutcomeBody? Count);

    private sealed record PlanLine(
        Guid Id,
        string Sku,
        string? LotNumber,
        string LocationCode,
        decimal ExpectedQuantity,
        int Rank,
        decimal Score,
        Guid? WorkTaskId,
        string? State,
        decimal? CountedQuantity,
        decimal? Variance)
    {
        /// <summary>Filled in by the test helper; not part of the wire shape.</summary>
        public Guid PlanId { get; init; }
    }

    private sealed record CountPlanBody(
        Guid Id,
        string Reference,
        string Status,
        int RequestedTasks,
        int RaisedCount,
        int CompletedCount,
        int VarianceCount,
        Guid DecisionTraceId,
        List<PlanLine> Lines);

    private sealed record CancelPlanBody(Guid Id, string Reference, int WithdrawnCount);

    private sealed record PreviewCountLineBody(string LocationCode, decimal ExpectedQuantity, int Rank);

    private sealed record PreviewCountBody(
        Guid DecisionTraceId,
        List<PreviewCountLineBody> Lines,
        TraceView Trace);

    private sealed record NoteBody(string Code, Dictionary<string, string> Args);

    private sealed record ScoreLineBody(string RuleCode, decimal Weight, decimal Value, NoteBody Reason);

    private sealed record TracedCandidateBody(
        string CandidateKey,
        int Rank,
        bool IsAdmissible,
        decimal Score,
        NoteBody? ExcludedBy,
        List<ScoreLineBody> Scores);

    private sealed record TraceView(
        Guid Id,
        string Kind,
        string RuleSetName,
        int ConsideredCount,
        int AdmissibleCount,
        string? SelectedKey,
        List<TracedCandidateBody> Candidates);

    private sealed record AccuracySliceBody(
        string Key,
        int Counted,
        int Accurate,
        decimal NetVariance,
        decimal AbsoluteVariance,
        decimal? AccuracyRate);

    private sealed record AccuracyBody(
        AccuracySliceBody Overall,
        List<AccuracySliceBody> ByZone,
        List<AccuracySliceBody> ByClass);

    private sealed record WorkKindStatisticsBody(
        string Kind,
        int Completed,
        int Overridden,
        int Cancelled,
        int Open,
        decimal? OverrideRate);

    private sealed record WorkStatisticsBody(List<WorkKindStatisticsBody> ByKind);

    private sealed record ReconciliationRunBody(
        Guid Id,
        string Trigger,
        long MovementsReplayed,
        int HoldingsChecked,
        decimal LedgerSum,
        int DriftCount,
        bool IsClean);

    private sealed record StrandedBody(
        Guid ReservationId,
        string OwnerReference,
        string Sku,
        string LocationCode,
        decimal ReservedQuantity,
        decimal OnHandQuantity,
        decimal Shortfall);

    private sealed record MovementReceiptBody(
        Guid MovementId,
        decimal Quantity,
        string FromLocationCode,
        string ToLocationCode);

    private sealed record OrderBody(Guid Id, string Reference, string Status);

    private sealed record BalancePage(List<BalanceRow> Items);

    private sealed record BalanceRow(string LocationCode, decimal Quantity);

    private sealed record MovementPage(List<MovementRow> Items, int Total);

    private sealed record MovementRow(
        Guid Id,
        long Sequence,
        decimal Quantity,
        string FromLocationCode,
        string ToLocationCode,
        string Type,
        Guid? TaskId,
        Guid? DecisionTraceId);
}
