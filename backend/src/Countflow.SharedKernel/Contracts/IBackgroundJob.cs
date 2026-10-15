using System.ComponentModel.DataAnnotations;

namespace Countflow.SharedKernel.Contracts;

/// <summary>A scoped handler. Retried runs keep their identity; writes must use the supplied key.</summary>
public interface IBackgroundJob
{
    Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken);
}

public sealed record BackgroundJobRun(string Name, Guid Id, DateTimeOffset ScheduledAt)
{
    public string IdempotencyKey => $"job:{Name}:{Id:N}";
}

public class RecurringJobOptions
{
    public bool Enabled { get; set; }

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
}
