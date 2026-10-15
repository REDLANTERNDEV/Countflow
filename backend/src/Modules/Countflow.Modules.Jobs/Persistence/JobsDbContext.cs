using Countflow.Modules.Jobs.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Jobs.Persistence;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "jobs";
    public DbSet<JobState> JobStates => Set<JobState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(SchemaName);
        builder.Entity<JobState>(state =>
        {
            state.HasKey(job => job.Name);
            state.Property(job => job.Name).HasMaxLength(64);
            state.Property(job => job.AttemptId).IsConcurrencyToken();
            state.Property(job => job.LastErrorCode).HasMaxLength(64);
            state.ToTable(table => table.HasCheckConstraint("CK_JobStates_PendingRunHasSchedule",
                "(\"PendingRunId\" IS NULL) = (\"PendingScheduledAt\" IS NULL)"));
        });
    }
}
