using Countflow.Modules.Replenishment.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Replenishment.Persistence;

public sealed class ReplenishmentDbContext(DbContextOptions<ReplenishmentDbContext> options) : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "replenishment";
    public DbSet<PickFacePolicy> PickFacePolicies => Set<PickFacePolicy>();
    public DbSet<ReplenishmentPlan> Plans => Set<ReplenishmentPlan>();
    public DbSet<ReplenishmentLine> Lines => Set<ReplenishmentLine>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<PickFacePolicy>()) entry.Entity.Revision = Guid.CreateVersion7();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(SchemaName);
        builder.Entity<PickFacePolicy>(policy =>
        {
            policy.HasKey(row => row.LocationId);
            policy.Property(row => row.Revision).IsConcurrencyToken();
            policy.Property(row => row.Minimum).HasPrecision(18, 4);
            policy.Property(row => row.Maximum).HasPrecision(18, 4);
            policy.Property(row => row.LastShortfall).HasPrecision(18, 4);
            policy.Property(row => row.LastErrorCode).HasMaxLength(128);
            policy.HasIndex(row => row.WarehouseId);
            policy.ToTable(table => table.HasCheckConstraint("CK_PickFacePolicies_Levels", "\"Minimum\" >= 0 AND \"Maximum\" > \"Minimum\""));
        });
        builder.Entity<ReplenishmentPlan>(plan =>
        {
            plan.HasKey(row => row.Id);
            plan.Property(row => row.Number).UseIdentityByDefaultColumn();
            plan.HasIndex(row => row.Number).IsUnique();
            plan.Ignore(row => row.Reference);
            plan.Property(row => row.RequestedQuantity).HasPrecision(18, 4);
            plan.HasMany(row => row.Lines).WithOne().HasForeignKey(row => row.PlanId).OnDelete(DeleteBehavior.Cascade);
            plan.HasIndex(row => new { row.LocationId, row.Status });
        });
        builder.Entity<ReplenishmentLine>(line =>
        {
            line.HasKey(row => row.Id);
            line.Property(row => row.Quantity).HasPrecision(18, 4);
            line.Property(row => row.ErrorCode).HasMaxLength(128);
            line.HasIndex(row => new { row.PlanId, row.BalanceId }).IsUnique();
            line.ToTable(table => table.HasCheckConstraint("CK_ReplenishmentLines_Positive", "\"Quantity\" > 0"));
        });
    }
}
