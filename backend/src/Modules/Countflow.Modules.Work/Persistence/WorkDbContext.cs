using Countflow.Modules.Work.Domain;
using Countflow.SharedKernel.Contracts;
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

    /// <summary>Picks claimed together and walked in an order. See <see cref="PickTour"/>.</summary>
    public DbSet<PickTour> PickTours => Set<PickTour>();

    public DbSet<PickTourSegment> PickTourSegments => Set<PickTourSegment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<WorkTask>(task =>
        {
            task.HasKey(t => t.Id);

            task.HasIndex(t => new { t.WarehouseId, t.CompletedAt }).HasFilter($"\"Status\" = {(int)WorkTaskStatus.Completed}").IncludeProperties(t => t.Kind);
            task.Property(t => t.Quantity).HasPrecision(18, 4);
            task.Property(t => t.CountedQuantity).HasPrecision(18, 4);
            task.Property(t => t.CompletionIdempotencyKey).HasMaxLength(128);
            task.Property(t => t.SourceReference).HasMaxLength(256);
            task.Property(t => t.AssignedToUserName).HasMaxLength(256);
            task.Property(t => t.CompletedByUserName).HasMaxLength(256);
            task.Property(t => t.OverrideReason).HasMaxLength(500);
            task.Property(t => t.CancelledReason).HasMaxLength(500);

            task.HasMany(t => t.Sources)
                .WithOne(s => s.Task!)
                .HasForeignKey(s => s.WorkTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // One open putaway per pallet per origin — the rule that stops a mixed
            // pallet raising a job per SKU. Enforced by the database rather than
            // by the enqueue path, because two receipts arriving together would
            // otherwise both find nothing and both insert.
            //
            // Narrowed to putaway in Phase 4. A pick names a pallet too — the
            // operator has to know which one to break into — but two orders drawing
            // on one pallet are two jobs, and without the kind in the filter the
            // second order's pick would merge into the first order's and be
            // confirmed against the wrong document.
            task.HasIndex(t => new { t.LicensePlateId, t.FromLocationId, t.Kind })
                .IsUnique()
                .HasFilter(
                    $"\"LicensePlateId\" IS NOT NULL AND \"Kind\" = {(int)WorkTaskKind.Putaway} " +
                    $"AND \"Status\" IN ({(int)WorkTaskStatus.Ready}, {(int)WorkTaskStatus.Assigned})");

            // The queue read: ready work for a warehouse, best first. Covers the
            // handheld's claim-next, which is the hottest query in the building
            // once a shift starts.
            task.HasIndex(t => new { t.WarehouseId, t.Status, t.Priority, t.CreatedAt });

            // "What am I holding?" — one operator's open work.
            task.HasIndex(t => new { t.AssignedToUserId, t.Status });

            task.HasIndex(t => t.DecisionTraceId);
            task.HasIndex(t => t.SourceReference);

            // SetNull rather than Cascade: handing a stop back clears the tour off
            // the task, and deleting a tour must never take the work with it. A tour
            // is a record of a walk; the picks are the work itself.
            task.HasOne(t => t.Tour)
                .WithMany(t => t.Tasks)
                .HasForeignKey(t => t.PickTourId)
                .OnDelete(DeleteBehavior.SetNull);

            // The walk, in order. Read once per stop by the handheld and once per
            // tour by the console.
            task.HasIndex(t => new { t.PickTourId, t.TourSequence });

            // A commitment is discharged by exactly one job. Two picks pointing at
            // one reservation would be refused by the ledger when the second tried
            // to consume it — this refuses them a step earlier, where the answer is
            // still "that job already exists" rather than "that stock has gone".
            task.HasIndex(t => t.StockReservationId)
                .IsUnique()
                .HasFilter("\"StockReservationId\" IS NOT NULL");

            task.ToTable(t =>
            {
                t.HasCheckConstraint("CK_WorkTasks_PositiveQuantity",
                    $"\"Kind\" = {(int)WorkTaskKind.BinCount} AND \"Quantity\" = 0 AND \"ItemId\" IS NULL AND \"LotId\" IS NULL AND \"LicensePlateId\" IS NULL OR \"Kind\" <> {(int)WorkTaskKind.BinCount} AND \"Quantity\" > 0 AND \"ItemId\" IS NOT NULL");
                t.HasCheckConstraint("CK_WorkTasks_BinCountClaim", "(\"BinCountVersion\" IS NULL) = (\"BinCountClaimId\" IS NULL)");

                // A stop with no place in the walk, or a place in a walk it is not
                // on, is a pick list that renders with a gap in it. The two columns
                // are set and cleared together in every path that touches them, and
                // this is what makes "every path" true rather than intended.
                t.HasCheckConstraint(
                    "CK_WorkTasks_TourSequenceBelongsToTour",
                    "(\"PickTourId\" IS NULL) = (\"TourSequence\" IS NULL)");

                // A completed task has to say where it was completed, or the
                // override statistics that justify the rules engine are computed
                // from a column that is sometimes null for no reason.
                t.HasCheckConstraint(
                    "CK_WorkTasks_CompletedHasLocation",
                    $"""
                     "Status" <> {(int)WorkTaskStatus.Completed}
                     OR ("CompletedAtLocationId" IS NOT NULL AND "CompletedAt" IS NOT NULL)
                     """);

                // A completed count has to carry the number it found, and nothing
                // else may carry one. The first half is the count's equivalent of
                // the rule above — a count with no finding is a bay marked verified
                // against nothing — and the second is what stops the column
                // quietly becoming a general-purpose extra quantity that some
                // future kind writes to and some future report reads.
                t.HasCheckConstraint(
                    "CK_WorkTasks_CountedQuantityBelongsToCounts",
                    $"""
                     ("Kind" = {(int)WorkTaskKind.CycleCount}
                      OR "CountedQuantity" IS NULL)
                     AND ("Kind" <> {(int)WorkTaskKind.CycleCount}
                          OR "Status" <> {(int)WorkTaskStatus.Completed}
                          OR "CountedQuantity" IS NOT NULL)
                     """);
            });
        });

        builder.Entity<PickTour>(tour =>
        {
            tour.HasKey(t => t.Id);

            tour.Property(t => t.AssignedToUserName).HasMaxLength(256).IsRequired();
            tour.Property(t => t.ClaimIdempotencyKey).HasMaxLength(128);
            tour.Property(t => t.ReleaseIdempotencyKey).HasMaxLength(128);
            tour.HasIndex(t => new { t.AssignedToUserId, t.ClaimIdempotencyKey })
                .IsUnique().HasFilter("\"ClaimIdempotencyKey\" IS NOT NULL");

            tour.HasMany(t => t.Segments)
                .WithOne(s => s.Tour!)
                .HasForeignKey(s => s.PickTourId)
                .OnDelete(DeleteBehavior.Cascade);

            // A tour is claimed by one person and read back by them for the length of
            // one walk, so the only listing that matters is "what am I on".
            tour.HasIndex(t => new { t.AssignedToUserId, t.ClaimedAt });

            tour.ToTable(t => t.HasCheckConstraint("CK_PickTours_PositiveStops", "\"StopCount\" > 0"));
        });

        builder.Entity<PickTourSegment>(segment =>
        {
            segment.HasKey(s => s.Id);
            segment.Property(s => s.ZoneCode).HasMaxLength(8);
            segment.Property(s => s.RequestedStrategy).HasMaxLength(64).IsRequired();
            segment.Property(s => s.AppliedStrategy).HasMaxLength(64).IsRequired();
            segment.Property(s => s.FellBackBecause).HasMaxLength(500);
        });

        builder.Entity<WorkTaskSource>(source =>
        {
            source.HasKey(s => s.Id);

            // 256 from Phase 4. A putaway's key is one level deep; a pick's is
            // derived from a wave's key, then an order's, then the commitment it
            // discharges, and the whole chain is what makes a retried release
            // raise the job exactly once.
            source.Property(s => s.IdempotencyKey).HasMaxLength(256).IsRequired();
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
