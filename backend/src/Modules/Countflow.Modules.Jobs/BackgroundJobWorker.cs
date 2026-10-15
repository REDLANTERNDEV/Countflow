using System.ComponentModel.DataAnnotations;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Countflow.Modules.Jobs;

public sealed class JobRunnerOptions
{
    public bool Enabled { get; set; } = true;
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class BackgroundJobWorker(
    BackgroundJobRunner runner, IEnumerable<BackgroundJobRegistration> jobs,
    IServiceScopeFactory scopes, IOptions<JobRunnerOptions> options, TimeProvider clock,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Background jobs are disabled");
            return;
        }
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(options.Value.PollInterval, clock);
        try
        {
            do
            {
                var discovered = new List<BackgroundJobRegistration>(jobs);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    foreach (var catalog in scope.ServiceProvider.GetServices<IBackgroundJobCatalog>())
                        discovered.AddRange(await catalog.ListAsync(stoppingToken));
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Configured background jobs could not be discovered");
                }
                foreach (var job in discovered.DistinctBy(job => job.Name))
                {
                    if (running.TryGetValue(job.Name, out var previous))
                    {
                        if (!previous.IsCompleted) continue;
                        await previous;
                    }
                    running[job.Name] = runner.RunJobAsync(job, stoppingToken);
                }
                foreach (var name in running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
                {
                    await running[name];
                    running.Remove(name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(running.Values); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
