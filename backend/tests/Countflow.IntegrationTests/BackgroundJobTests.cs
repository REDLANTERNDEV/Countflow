using System.Collections.Concurrent;
using Countflow.Modules.Jobs;
using Countflow.Modules.Jobs.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class BackgroundJobTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public async Task A_due_job_runs_in_a_scope_and_persists_its_next_due_time()
    {
        var clock = new ManualClock();
        var probe = new Probe();
        var name = $"test.job.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, clock, probe);
        var runner = host.Services.GetRequiredService<BackgroundJobRunner>();
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Single(probe.Runs);
        using var scope = host.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<JobsDbContext>().JobStates.FindAsync(name);
        Assert.NotNull(state);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 1, 0, TimeSpan.Zero), state.NextRunAt);
        var startedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(startedAt, state.LastStartedAt);
        Assert.Equal(startedAt, state.LastFinishedAt);
        Assert.Equal(startedAt, Assert.Single(probe.Runs).ScheduledAt);
        Assert.Null(state.PendingRunId);
        Assert.NotNull(state.LastFinishedAt);
    }

    [Fact]
    public async Task A_failed_run_retries_with_the_same_identity_after_backoff()
    {
        var clock = new ManualClock();
        var probe = new Probe { Fail = true };
        var name = $"test.retry.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, clock, probe);
        var runner = host.Services.GetRequiredService<BackgroundJobRunner>();
        await runner.RunDueAsync(CancellationToken.None);
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Single(probe.Runs);
        using (var failedScope = host.Services.CreateScope())
        {
            var failed = await failedScope.ServiceProvider.GetRequiredService<JobsDbContext>().JobStates.FindAsync(name);
            Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 30, TimeSpan.Zero), failed!.NextRunAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), failed.PendingScheduledAt);
            Assert.Equal(failed.PendingScheduledAt, failed.LastStartedAt);
            Assert.Equal(failed.PendingScheduledAt, failed.LastFinishedAt);
        }
        probe.Fail = false;
        clock.Advance(TimeSpan.FromSeconds(31));
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, probe.Runs.Count);
        Assert.Equal(probe.Runs.First(), probe.Runs.Last());
        using var scope = host.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<JobsDbContext>().JobStates.FindAsync(name);
        Assert.Null(state!.PendingRunId);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Two_dispatchers_cannot_run_the_same_job_together()
    {
        var probe = new Probe { Block = true };
        var name = $"test.lock.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, new ManualClock(), probe);
        var runner = host.Services.GetRequiredService<BackgroundJobRunner>();
        var first = runner.RunDueAsync(CancellationToken.None);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await runner.RunDueAsync(CancellationToken.None);
            Assert.Single(probe.Runs);
        }
        finally { probe.Resume.TrySetResult(); }
        await first;
    }

    [Fact]
    public async Task Cancellation_and_restart_resume_the_persisted_run_identity()
    {
        var clock = new ManualClock();
        var interrupted = new Probe { Block = true };
        var name = $"test.restart.{Guid.CreateVersion7():N}";
        await using (var host = CreateHost(name, clock, interrupted))
        {
            using var cancellation = new CancellationTokenSource();
            var running = host.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(cancellation.Token);
            await interrupted.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        var resumed = new Probe();
        await using var restarted = CreateHost(name, clock, resumed);
        var runner = restarted.Services.GetRequiredService<BackgroundJobRunner>();
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Equal(Assert.Single(interrupted.Runs), Assert.Single(resumed.Runs));
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Single(resumed.Runs);
        clock.Advance(TimeSpan.FromMinutes(1));
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, resumed.Runs.Count);
        Assert.NotEqual(resumed.Runs.First().Id, resumed.Runs.Last().Id);
    }

    [Fact]
    public async Task A_failed_job_does_not_prevent_another_job_from_running()
    {
        var clock = new ManualClock();
        var probe = new Probe { Fail = true };
        var name = $"test.isolation.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, clock, probe).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddRecurringJob<HealthyJob>(
                $"{name}.ok", new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "Healthy",
                options => { options.Enabled = true; options.InitialDelay = TimeSpan.Zero; })));
        await host.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(CancellationToken.None);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        var failed = await db.JobStates.FindAsync(name);
        var healthy = await db.JobStates.FindAsync($"{name}.ok");
        Assert.Equal(1, failed!.ConsecutiveFailures);
        Assert.NotNull(failed.PendingRunId);
        Assert.Equal("jobs.execution_failed", failed.LastErrorCode);
        Assert.NotNull(healthy!.LastFinishedAt);
        Assert.Null(healthy.PendingRunId);
    }

    [Fact]
    public async Task A_blocked_job_does_not_delay_another_jobs_next_run()
    {
        var clock = new ManualClock();
        var probe = new Probe { Block = true };
        var name = $"test.cadence.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, clock, probe).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddRecurringJob<HealthyJob>(
                $"{name}.ok", new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "Healthy",
                options => { options.Enabled = true; options.InitialDelay = TimeSpan.Zero; options.Interval = TimeSpan.FromMinutes(1); })));
        var services = host.Services;
        var healthy = services.GetRequiredService<HealthyProbe>();
        using var worker = new BackgroundJobWorker(services.GetRequiredService<BackgroundJobRunner>(),
            services.GetServices<BackgroundJobRegistration>().Where(job => job.Name.StartsWith(name, StringComparison.Ordinal)),
            services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new JobRunnerOptions { PollInterval = TimeSpan.FromSeconds(1) }),
            clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundJobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await healthy.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromMinutes(1));
            await healthy.Second.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(probe.Runs);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(shutdown.Token);
        }
    }

    [Fact]
    public async Task A_stale_attempt_cannot_erase_a_new_run_after_losing_its_lock()
    {
        var clock = new ManualClock();
        var firstProbe = new Probe { Block = true };
        var name = $"test.fence.{Guid.CreateVersion7():N}";
        await using var firstHost = CreateHost(name, clock, firstProbe);
        var first = firstHost.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(CancellationToken.None);
        await firstProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = firstHost.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JobsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {$"countflow.job:{name}"}");

        var replacementProbe = new Probe();
        await using var replacement = CreateHost(name, clock, replacementProbe);
        await replacement.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(CancellationToken.None);
        Assert.Equal(Assert.Single(firstProbe.Runs).Id, Assert.Single(replacementProbe.Runs).Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        var nextProbe = new Probe { Block = true };
        await using var nextHost = CreateHost(name, clock, nextProbe);
        var next = nextHost.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(CancellationToken.None);
        await nextProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            firstProbe.Resume.TrySetResult();
            await first;
            using var scope = firstHost.Services.CreateScope();
            var state = await scope.ServiceProvider.GetRequiredService<JobsDbContext>().JobStates.FindAsync(name);
            Assert.Equal(Assert.Single(nextProbe.Runs).Id, state!.PendingRunId);
        }
        finally
        {
            firstProbe.Resume.TrySetResult();
            nextProbe.Resume.TrySetResult();
            await Task.WhenAll(first, next);
        }
    }

    [Fact]
    public async Task Disabled_jobs_do_not_run_or_create_scheduling_state()
    {
        var probe = new Probe();
        var name = $"test.off.{Guid.CreateVersion7():N}";
        await using var host = CreateHost(name, new ManualClock(), probe, enabled: false);
        await host.Services.GetRequiredService<BackgroundJobRunner>().RunDueAsync(CancellationToken.None);
        Assert.Empty(probe.Runs);
        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<JobsDbContext>().JobStates.FindAsync(name));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateHost(
        string name, ManualClock clock, Probe probe, bool enabled = true) => fixture.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Jobs:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(probe);
            services.AddSingleton<HealthyProbe>();
            services.AddRecurringJob<ProbeJob>(name, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                "Probe", options => { options.Enabled = enabled; options.Interval = TimeSpan.FromMinutes(1); options.InitialDelay = TimeSpan.Zero; });
        });
    });

    private sealed class ManualClock : TimeProvider
    {
        // Keep sub-microsecond ticks so PostgreSQL round trips exercise the
        // precision boundary on every platform, including macOS clocks.
        private DateTimeOffset _now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class Probe
    {
        public ConcurrentQueue<BackgroundJobRun> Runs { get; } = new();
        public bool Fail { get; set; }
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProbeJob(Probe probe) : IBackgroundJob
    {
        public async Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken)
        {
            probe.Runs.Enqueue(run);
            probe.Started.TrySetResult();
            if (probe.Block) await probe.Resume.Task.WaitAsync(cancellationToken);
            if (probe.Fail) throw new InvalidOperationException("Intentional job failure");
        }
    }

    private sealed class HealthyProbe
    {
        public int Runs;
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HealthyJob(HealthyProbe probe) : IBackgroundJob
    {
        public Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref probe.Runs) == 1) probe.First.TrySetResult();
            else probe.Second.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
