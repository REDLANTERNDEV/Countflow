using Countflow.Modules.Counting.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Counting.Persistence;

public sealed class CountingDbContext(DbContextOptions<CountingDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "counting";

    public DbSet<CountPlanningFence> CountPlanningFences => Set<CountPlanningFence>();

    public DbSet<CountSchedule> CountSchedules => Set<CountSchedule>();

    public DbSet<CountPlan> CountPlans => Set<CountPlan>();

    public DbSet<CountPlanLine> CountPlanLines => Set<CountPlanLine>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // The lease attaches one fence. Rotate it in the same EF transaction as
        // every plan/line/schedule write; a rival lease makes stale saves fail.
        foreach (var entry in ChangeTracker.Entries<CountPlanningFence>())
            entry.Entity.Revision = Guid.CreateVersion7();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<CountPlanningFence>(fence =>
        {
            fence.HasKey(row => row.WarehouseId);
            fence.Property(row => row.Revision).IsConcurrencyToken();
        });

        builder.Entity<CountSchedule>(schedule =>
        {
            schedule.HasKey(row => row.WarehouseId);
            schedule.Property(row => row.Revision).IsConcurrencyToken();
            schedule.Property(row => row.LastErrorCode).HasMaxLength(64);
            schedule.ToTable(table =>
            {
                table.HasCheckConstraint("CK_CountSchedules_Budget", "\"MaxTasks\" BETWEEN 1 AND 200");
                table.HasCheckConstraint("CK_CountSchedules_Interval", "\"IntervalMinutes\" BETWEEN 1 AND 10080");
                table.HasCheckConstraint("CK_CountSchedules_Pending", "(\"PendingRunId\" IS NULL) = (\"PendingScheduledAt\" IS NULL)");
            });
        });

        builder.Entity<CountPlan>(plan =>
        {
            plan.HasKey(p => p.Id);
            plan.Property(p => p.Reference).HasMaxLength(64).IsRequired();
            plan.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
            plan.Property(p => p.CreatedByUserName).HasMaxLength(256).IsRequired();
            plan.Property(p => p.Notes).HasMaxLength(1000);
            plan.Property(p => p.CancelledReason).HasMaxLength(500);

            plan.HasMany(p => p.Lines)
                .WithOne(l => l.Plan!)
                .HasForeignKey(l => l.CountPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            // Plan numbers are unique within a warehouse's own book of them, the
            // same rule advice notes and sales orders follow.
            plan.HasIndex(p => new { p.WarehouseId, p.Reference }).IsUnique();

            // A retried planning request converges on the plan it already made
            // rather than running the policy a second time. Without this the retry
            // would produce a fresh trace, a fresh ranking and a second set of jobs
            // against the same bays.
            plan.HasIndex(p => p.IdempotencyKey).IsUnique();

            plan.HasIndex(p => new { p.WarehouseId, p.Status, p.CreatedAt });
        });

        builder.Entity<CountPlanLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.ExpectedQuantity).HasPrecision(18, 4);
            line.Property(l => l.Score).HasPrecision(9, 6);
            line.Property(l => l.LocationCode).HasMaxLength(64).IsRequired();

            // 256, matching the work source key it becomes: a line's key descends
            // from its plan's, exactly as a pick's descends from a wave's.
            line.Property(l => l.IdempotencyKey).HasMaxLength(256).IsRequired();

            line.HasIndex(l => l.IdempotencyKey).IsUnique();

            // The policy's already-scheduled exclusion, read once per planning run:
            // which holdings have a line whose job is still open. Filtered on the
            // plan rather than the task because the plan is what this module owns —
            // what became of the job is Work's answer, asked separately.
            line.HasIndex(l => new { l.CountPlanId, l.BalanceId });
            line.HasIndex(l => l.BalanceId);
            line.HasIndex(l => l.WorkTaskId);

            line.ToTable(table =>
                table.HasCheckConstraint("CK_CountPlanLines_NonNegativeExpected", "\"ExpectedQuantity\" >= 0"));
        });

        // No foreign keys to inventory.StockBalances, work.WorkTasks or
        // rules.DecisionTraces, by design (ADR 0002). A line names all three by id
        // and asks their owners what became of them — which is what keeps this
        // module an orchestrator rather than a second copy of the counting loop.
    }
}
