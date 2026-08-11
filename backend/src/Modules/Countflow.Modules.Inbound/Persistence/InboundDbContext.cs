using Countflow.Modules.Inbound.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inbound.Persistence;

public sealed class InboundDbContext(DbContextOptions<InboundDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "inbound";

    public DbSet<Asn> Asns => Set<Asn>();

    public DbSet<AsnLine> AsnLines => Set<AsnLine>();

    /// <summary>What has actually been booked in. <see cref="AsnLine.ReceivedQuantity"/> is derived from these.</summary>
    public DbSet<AsnReceipt> AsnReceipts => Set<AsnReceipt>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<Asn>(asn =>
        {
            asn.HasKey(a => a.Id);
            asn.Property(a => a.Reference).HasMaxLength(64).IsRequired();
            asn.Property(a => a.SupplierName).HasMaxLength(256);
            asn.Property(a => a.CarrierReference).HasMaxLength(64);
            asn.Property(a => a.Notes).HasMaxLength(1000);

            asn.HasMany(a => a.Lines)
                .WithOne(l => l.Asn!)
                .HasForeignKey(l => l.AsnId)
                .OnDelete(DeleteBehavior.Cascade);

            // Supplier references repeat across suppliers constantly; they are
            // unique only within a warehouse's book of them.
            asn.HasIndex(a => new { a.WarehouseId, a.Reference }).IsUnique();
            asn.HasIndex(a => new { a.Status, a.ExpectedAt });
        });

        builder.Entity<AsnLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.ExpectedQuantity).HasPrecision(18, 4);
            line.Property(l => l.ReceivedQuantity).HasPrecision(18, 4);
            line.Property(l => l.ExpectedLotNumber).HasMaxLength(64);

            line.HasIndex(l => new { l.AsnId, l.LineNumber }).IsUnique();
            line.HasIndex(l => l.ItemId);

            line.ToTable(table =>
            {
                table.HasCheckConstraint("CK_AsnLines_PositiveExpected", "\"ExpectedQuantity\" > 0");

                // Over-delivery is allowed; negative receipts are not. A supplier
                // sending more than they said is a business problem, but a line
                // that has received a negative amount is a bug.
                table.HasCheckConstraint("CK_AsnLines_NonNegativeReceived", "\"ReceivedQuantity\" >= 0");
            });
        });

        builder.Entity<AsnReceipt>(receipt =>
        {
            receipt.HasKey(r => r.Id);
            receipt.Property(r => r.Quantity).HasPrecision(18, 4);
            receipt.Property(r => r.IdempotencyKey).HasMaxLength(128).IsRequired();
            receipt.Property(r => r.ReceivedByUserName).HasMaxLength(256).IsRequired();

            receipt.HasOne(r => r.Line!)
                .WithMany(l => l.Receipts)
                .HasForeignKey(r => r.AsnLineId)
                .OnDelete(DeleteBehavior.Cascade);

            // Exactly-once, in one line. A retried dock scan collides here rather
            // than adding the quantity a second time.
            receipt.HasIndex(r => r.IdempotencyKey).IsUnique();
            receipt.HasIndex(r => r.MovementId);
        });

        // No foreign keys to masterdata.Items, work.WorkTasks or the ledger, by
        // design (ADR 0002). What ties a receipt to its line is the reference
        // carried on the movement and the task, which survives both modules being
        // deployed apart.
    }
}
