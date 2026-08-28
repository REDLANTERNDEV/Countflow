using Countflow.Modules.MasterData.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Persistence;

public sealed class MasterDataDbContext(DbContextOptions<MasterDataDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "masterdata";

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<Area> Areas => Set<Area>();

    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Item> Items => Set<Item>();

    public DbSet<PackConfiguration> PackConfigurations => Set<PackConfiguration>();

    public DbSet<BusinessPartner> BusinessPartners => Set<BusinessPartner>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        // The SSCC serial reference, and the one number in this system that is
        // deliberately not transactional. A sequence keeps counting through a
        // rollback, which everywhere else would be a leak and here is the
        // requirement: a serial handed out and then abandoned must never come
        // back, because the label may already have been printed. See ISsccMint.
        builder.HasSequence<long>(Integration.SsccMint.SequenceName, SchemaName).StartsAt(1).IncrementsBy(1);

        builder.Entity<Warehouse>(warehouse =>
        {
            warehouse.HasKey(w => w.Id);
            warehouse.Property(w => w.Code).HasMaxLength(16).IsRequired();
            warehouse.Property(w => w.Name).HasMaxLength(200).IsRequired();
            warehouse.Property(w => w.City).HasMaxLength(100);
            warehouse.Property(w => w.CountryCode).HasMaxLength(2);
            warehouse.Property(w => w.Gs1CompanyPrefix).HasMaxLength(10);
            warehouse.HasIndex(w => w.Code).IsUnique();
        });

        builder.Entity<Area>(area =>
        {
            area.HasKey(a => a.Id);
            area.Property(a => a.Code).HasMaxLength(8).IsRequired();
            area.Property(a => a.Name).HasMaxLength(200).IsRequired();
            area.HasIndex(a => new { a.WarehouseId, a.Code }).IsUnique();

            area.HasOne(a => a.Warehouse)
                .WithMany(w => w.Areas)
                .HasForeignKey(a => a.WarehouseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Zone>(zone =>
        {
            zone.HasKey(z => z.Id);
            zone.Property(z => z.Code).HasMaxLength(8).IsRequired();
            zone.Property(z => z.Name).HasMaxLength(200).IsRequired();
            zone.HasIndex(z => new { z.AreaId, z.Code }).IsUnique();

            zone.HasOne(z => z.Area)
                .WithMany(a => a.Zones)
                .HasForeignKey(z => z.AreaId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Location>(location =>
        {
            location.HasKey(l => l.Id);
            location.Property(l => l.Code).HasMaxLength(64).IsRequired();
            location.Property(l => l.Aisle).HasMaxLength(8);
            location.Property(l => l.Bay).HasMaxLength(8);
            location.Property(l => l.Level).HasMaxLength(8);
            location.Property(l => l.Position).HasMaxLength(8);
            location.Property(l => l.MaxVolumeM3).HasPrecision(12, 6);
            location.Property(l => l.MaxWeightKg).HasPrecision(12, 3);

            // The code is the natural key operators actually use, so it must be
            // globally unique rather than merely unique within a warehouse.
            location.HasIndex(l => l.Code).IsUnique();

            // Pick routing sorts on this constantly; it is the hot read path
            // once wave picking arrives.
            location.HasIndex(l => new { l.ZoneId, l.PickSequence });

            location.HasIndex(l => new { l.WarehouseId, l.Type });

            location.HasOne(l => l.Warehouse)
                .WithMany(w => w.Locations)
                .HasForeignKey(l => l.WarehouseId)
                .OnDelete(DeleteBehavior.Restrict);

            location.HasOne(l => l.Zone)
                .WithMany(z => z.Locations)
                .HasForeignKey(l => l.ZoneId)
                .OnDelete(DeleteBehavior.Restrict);

            location.Ignore(l => l.IsVirtual);
        });

        builder.Entity<Item>(item =>
        {
            item.HasKey(i => i.Id);
            item.Property(i => i.Sku).HasMaxLength(64).IsRequired();
            item.Property(i => i.Description).HasMaxLength(400).IsRequired();
            item.Property(i => i.BaseUom).HasMaxLength(8).IsRequired();
            item.HasIndex(i => i.Sku).IsUnique();
            item.HasIndex(i => i.AbcClass);
        });

        builder.Entity<PackConfiguration>(pack =>
        {
            pack.HasKey(p => p.Id);
            pack.Property(p => p.Gtin).HasMaxLength(14).IsFixedLength();
            pack.Property(p => p.LengthMm).HasPrecision(10, 2);
            pack.Property(p => p.WidthMm).HasPrecision(10, 2);
            pack.Property(p => p.HeightMm).HasPrecision(10, 2);
            pack.Property(p => p.GrossWeightKg).HasPrecision(12, 3);

            pack.HasIndex(p => new { p.ItemId, p.Level }).IsUnique();

            // Scanning a barcode must resolve to exactly one packaging level, or
            // the receiver cannot know whether they scanned a case or a pallet.
            // Filtered so the many items without a GTIN at some level do not all
            // collide on null.
            pack.HasIndex(p => p.Gtin)
                .IsUnique()
                .HasFilter("\"Gtin\" IS NOT NULL");

            pack.HasOne(p => p.Item)
                .WithMany(i => i.PackConfigurations)
                .HasForeignKey(p => p.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            pack.Ignore(p => p.VolumeM3);
        });

        builder.Entity<BusinessPartner>(partner =>
        {
            partner.HasKey(p => p.Id);
            partner.Property(p => p.Code).HasMaxLength(32).IsRequired();
            partner.Property(p => p.Name).HasMaxLength(200).IsRequired();
            partner.Property(p => p.Gln).HasMaxLength(13);
            partner.Property(p => p.City).HasMaxLength(100);
            partner.Property(p => p.CountryCode).HasMaxLength(2);
            partner.HasIndex(p => p.Code).IsUnique();
        });
    }
}
