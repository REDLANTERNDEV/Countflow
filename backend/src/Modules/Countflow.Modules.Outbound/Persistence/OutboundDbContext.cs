using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Outbound.Persistence;

public sealed class OutboundDbContext(DbContextOptions<OutboundDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "outbound";

    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();

    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

    /// <summary>
    /// What has actually been committed. <see cref="SalesOrderLine.AllocatedQuantity"/>
    /// is derived from these.
    /// </summary>
    public DbSet<OrderLineAllocation> OrderLineAllocations => Set<OrderLineAllocation>();

    public DbSet<Wave> Waves => Set<Wave>();

    public DbSet<Return> Returns => Set<Return>();

    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();

    /// <summary>
    /// What has actually been decided and posted.
    /// <see cref="ReturnLine.DispositionedQuantity"/> is derived from these, and
    /// <see cref="SalesOrderLine.ReturnedQuantity"/> from the lines.
    /// </summary>
    public DbSet<ReturnLineDisposition> ReturnLineDispositions => Set<ReturnLineDisposition>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<SalesOrder>(order =>
        {
            order.HasKey(o => o.Id);
            order.Property(o => o.Reference).HasMaxLength(64).IsRequired();
            order.Property(o => o.CustomerName).HasMaxLength(256);
            order.Property(o => o.Notes).HasMaxLength(1000);
            order.Property(o => o.PackedByUserName).HasMaxLength(256);
            order.Property(o => o.DespatchedByUserName).HasMaxLength(256);
            order.Property(o => o.DespatchIdempotencyKey).HasMaxLength(128);
            order.Property(o => o.CancelledReason).HasMaxLength(500);

            order.HasMany(o => o.Lines)
                .WithOne(l => l.Order!)
                .HasForeignKey(l => l.SalesOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Order numbers repeat across customers; they are unique only within a
            // warehouse's own book of them — the same rule advice notes follow.
            order.HasIndex(o => new { o.WarehouseId, o.Reference }).IsUnique();
            order.HasIndex(o => new { o.Status, o.Priority, o.ShipBy });
            order.HasIndex(o => o.WaveId);
        });

        builder.Entity<SalesOrderLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.OrderedQuantity).HasPrecision(18, 4);
            line.Property(l => l.AllocatedQuantity).HasPrecision(18, 4);
            line.Property(l => l.ReturnedQuantity).HasPrecision(18, 4);

            line.HasMany(l => l.Allocations)
                .WithOne(a => a.Line!)
                .HasForeignKey(a => a.SalesOrderLineId)
                .OnDelete(DeleteBehavior.Cascade);

            line.HasIndex(l => new { l.SalesOrderId, l.LineNumber }).IsUnique();
            line.HasIndex(l => l.ItemId);

            line.ToTable(table =>
            {
                table.HasCheckConstraint("CK_SalesOrderLines_PositiveOrdered", "\"OrderedQuantity\" > 0");

                // Over-allocating is not a business problem the way over-delivery
                // is; it is a bug. A line committed more stock than it ordered has
                // taken somebody else's.
                table.HasCheckConstraint(
                    "CK_SalesOrderLines_AllocationWithinOrder",
                    "\"AllocatedQuantity\" >= 0 AND \"AllocatedQuantity\" <= \"OrderedQuantity\"");
            });
        });

        builder.Entity<OrderLineAllocation>(allocation =>
        {
            allocation.HasKey(a => a.Id);
            allocation.Property(a => a.Quantity).HasPrecision(18, 4);
            // 256, matching the reservation key it travels beside: a wave release
            // hands each order a key derived from its own.
            allocation.Property(a => a.ReleaseIdempotencyKey).HasMaxLength(256).IsRequired();
            allocation.Property(a => a.LocationCode).HasMaxLength(64).IsRequired();
            allocation.Property(a => a.LotNumber).HasMaxLength(64);

            // One row per commitment. A retried release that got as far as
            // reserving but not as far as writing this row finds the reservation
            // by its own key and writes the row on the second pass; a retry that
            // got all the way through collides here instead of committing the
            // stock twice.
            allocation.HasIndex(a => a.StockReservationId).IsUnique();

            allocation.HasIndex(a => new { a.SalesOrderLineId, a.ReleaseIdempotencyKey });
            allocation.HasIndex(a => a.WorkTaskId);

            allocation.ToTable(table =>
                table.HasCheckConstraint("CK_OrderLineAllocations_PositiveQuantity", "\"Quantity\" > 0"));
        });

        builder.Entity<Wave>(wave =>
        {
            wave.HasKey(w => w.Id);
            wave.Property(w => w.Reference).HasMaxLength(64).IsRequired();
            wave.Property(w => w.Notes).HasMaxLength(1000);
            wave.Property(w => w.ReleasedByUserName).HasMaxLength(256);

            wave.HasIndex(w => new { w.WarehouseId, w.Reference }).IsUnique();
            wave.HasIndex(w => w.Status);
        });

        builder.Entity<Return>(returned =>
        {
            returned.HasKey(r => r.Id);
            returned.Property(r => r.Reference).HasMaxLength(64).IsRequired();
            returned.Property(r => r.SalesOrderReference).HasMaxLength(64);
            returned.Property(r => r.CustomerName).HasMaxLength(256);
            returned.Property(r => r.CreatedByUserName).HasMaxLength(256);
            returned.Property(r => r.Notes).HasMaxLength(1000);

            returned.HasMany(r => r.Lines)
                .WithOne(l => l.Return!)
                .HasForeignKey(l => l.ReturnId)
                .OnDelete(DeleteBehavior.Cascade);

            // RMA numbers repeat across customers the way order numbers do; they
            // are unique only within a warehouse's own book of them.
            returned.HasIndex(r => new { r.WarehouseId, r.Reference }).IsUnique();
            returned.HasIndex(r => r.Status);
            returned.HasIndex(r => r.SalesOrderId);
        });

        builder.Entity<ReturnLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.Quantity).HasPrecision(18, 4);
            line.Property(l => l.DispositionedQuantity).HasPrecision(18, 4);
            line.Property(l => l.LotNumber).HasMaxLength(64);

            line.HasMany(l => l.Dispositions)
                .WithOne(d => d.Line!)
                .HasForeignKey(d => d.ReturnLineId)
                .OnDelete(DeleteBehavior.Cascade);

            line.HasIndex(l => new { l.ReturnId, l.LineNumber }).IsUnique();
            line.HasIndex(l => l.ItemId);
            line.HasIndex(l => l.SalesOrderLineId);

            // One receipt movement per line, and the ledger entry is what makes the
            // line a physical fact. Two lines claiming the same movement would be
            // one pallet booked in and counted twice.
            line.HasIndex(l => l.ReceiptMovementId).IsUnique();

            line.ToTable(table =>
            {
                table.HasCheckConstraint("CK_ReturnLines_PositiveQuantity", "\"Quantity\" > 0");

                // Dispositioning more than came back is not a business event the
                // way an over-delivery is; it is a bug, and it would put stock into
                // the warehouse that nobody ever received.
                table.HasCheckConstraint(
                    "CK_ReturnLines_DispositionWithinLine",
                    "\"DispositionedQuantity\" >= 0 AND \"DispositionedQuantity\" <= \"Quantity\"");
            });
        });

        builder.Entity<ReturnLineDisposition>(disposition =>
        {
            disposition.HasKey(d => d.Id);
            disposition.Property(d => d.Quantity).HasPrecision(18, 4);
            disposition.Property(d => d.IdempotencyKey).HasMaxLength(256).IsRequired();
            disposition.Property(d => d.DecidedByUserName).HasMaxLength(256);
            disposition.Property(d => d.Notes).HasMaxLength(1000);

            // A retry that got as far as posting the movement but not as far as
            // writing this row finds its own movement by key on the second pass; a
            // retry that got all the way through collides here instead of posting a
            // second write-off.
            disposition.HasIndex(d => d.IdempotencyKey).IsUnique();

            disposition.HasIndex(d => new { d.ReturnLineId, d.DecidedAt });
            disposition.HasIndex(d => d.DecisionTraceId);

            disposition.ToTable(table =>
                table.HasCheckConstraint("CK_ReturnLineDispositions_PositiveQuantity", "\"Quantity\" > 0"));
        });

        // No foreign keys to masterdata.Items, inventory.StockReservations or
        // work.WorkTasks, by design (ADR 0002). An allocation names all three by
        // id and asks their owners what became of them — which is what keeps this
        // module an orchestrator rather than a second copy of the outbound loop.
        // A return line names its receipt movement and its inspection task the same
        // way, and for the same reason.
    }
}
