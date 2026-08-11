using Countflow.Modules.Work.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Work.Persistence;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "work";

    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();

    /// <summary>What asked for each task. <see cref="WorkTask.Quantity"/> is derived from these.</summary>
    public DbSet<WorkTaskSource> WorkTaskSources => Set<WorkTaskSource>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<WorkTask>(task =>
        {
            task.HasKey(t => t.Id);

            task.Property(t => t.Quantity).HasPrecision(18, 4);
            task.Property(t => t.CompletionIdempotencyKey).HasMaxLength(128);
            task.Property(t => t.SourceReference).HasMaxLength(128);
            task.Property(t => t.AssignedToUserName).HasMaxLength(256);
            task.Property(t => t.CompletedByUserName).HasMaxLength(256);
            task.Property(t => t.OverrideReason).HasMaxLength(500);
            task.Property(t => t.CancelledReason).HasMaxLength(500);

            task.HasMany(t => t.Sources)
                .WithOne(s => s.Task!)
                .HasForeignKey(s => s.WorkTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // One open task per pallet per origin — the rule that stops a mixed
            // pallet raising a job per SKU. Enforced by the database rather than
            // by the enqueue path, because two receipts arriving together would
            // otherwise both find nothing and both insert.
            task.HasIndex(t => new { t.LicensePlateId, t.FromLocationId, t.Kind })
                .IsUnique()
                .HasFilter($"\"LicensePlateId\" IS NOT NULL AND \"Status\" IN ({(int)WorkTaskStatus.Ready}, {(int)WorkTaskStatus.Assigned})");

            // The queue read: ready work for a warehouse, best first. Covers the
            // handheld's claim-next, which is the hottest query in the building
            // once a shift starts.
            task.HasIndex(t => new { t.WarehouseId, t.Status, t.Priority, t.CreatedAt });

            // "What am I holding?" — one operator's open work.
            task.HasIndex(t => new { t.AssignedToUserId, t.Status });

            task.HasIndex(t => t.DecisionTraceId);
            task.HasIndex(t => t.SourceReference);

            task.ToTable(t =>
            {
                t.HasCheckConstraint("CK_WorkTasks_PositiveQuantity", "\"Quantity\" > 0");

                // A completed task has to say where it was completed, or the
                // override statistics that justify the rules engine are computed
                // from a column that is sometimes null for no reason.
                t.HasCheckConstraint(
                    "CK_WorkTasks_CompletedHasLocation",
                    $"""
                     "Status" <> {(int)WorkTaskStatus.Completed}
                     OR ("CompletedAtLocationId" IS NOT NULL AND "CompletedAt" IS NOT NULL)
                     """);
            });
        });

        builder.Entity<WorkTaskSource>(source =>
        {
            source.HasKey(s => s.Id);
            source.Property(s => s.IdempotencyKey).HasMaxLength(128).IsRequired();
            source.Property(s => s.Quantity).HasPrecision(18, 4);

            // Exactly-once enqueuing, in one line. A retried dock receipt collides
            // here rather than raising a second putaway or inflating an existing
            // one, and the collision is what lets the raiser recognise its own
            // earlier attempt.
            source.HasIndex(s => s.IdempotencyKey).IsUnique();
        });

        // No foreign keys to masterdata, inventory or rules, by design (ADR 0002).
        // A task names an item, a location and a trace by id and resolves them
        // through published contracts.
    }
}
