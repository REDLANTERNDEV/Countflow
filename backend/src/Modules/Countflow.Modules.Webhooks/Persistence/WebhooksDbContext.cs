using Countflow.Modules.Webhooks.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
namespace Countflow.Modules.Webhooks.Persistence;

public sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options) : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "webhooks";
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<InboundTrigger> Triggers => Set<InboundTrigger>();
    public DbSet<TriggerRequest> Requests => Set<TriggerRequest>();
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Subscription>()) entry.Entity.Revision = Guid.CreateVersion7();
        foreach (var entry in ChangeTracker.Entries<TriggerRequest>()) entry.Entity.Revision = Guid.CreateVersion7();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(SchemaName);
        builder.Entity<Subscription>(row => { row.HasKey(s => s.Id); row.Property(s => s.Revision).IsConcurrencyToken(); row.Property(s => s.Name).HasMaxLength(100); row.Property(s => s.Url).HasMaxLength(2048); row.Property(s => s.ProtectedSecret).HasMaxLength(256); });
        builder.Entity<Delivery>(row => { row.HasKey(d => d.Id); row.HasIndex(d => new { d.SubscriptionId, d.CreatedAt }); row.Property(d => d.LastErrorCode).HasMaxLength(128); row.HasOne<Subscription>().WithMany().HasForeignKey(d => d.SubscriptionId); });
        builder.Entity<InboundTrigger>(row => { row.HasKey(t => t.Id); row.Property(t => t.Name).HasMaxLength(100); row.Property(t => t.TokenHash).HasMaxLength(64); });
        builder.Entity<TriggerRequest>(row => { row.HasKey(r => r.Id); row.Property(r => r.Revision).IsConcurrencyToken(); row.Property(r => r.RetryKey).HasMaxLength(128); row.HasIndex(r => new { r.TriggerId, r.RetryKey }).IsUnique(); row.HasOne<InboundTrigger>().WithMany().HasForeignKey(r => r.TriggerId); });
    }
}
