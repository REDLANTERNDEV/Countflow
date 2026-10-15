namespace Countflow.Modules.Jobs.Domain;

/// <summary>Keep in-memory job state identical to PostgreSQL's microsecond representation.</summary>
internal static class JobTimestamp
{
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }
}
