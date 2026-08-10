using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Identity.Persistence;

public sealed class IdentityModuleDbContext(DbContextOptions<IdentityModuleDbContext> options)
    : IdentityDbContext<CountflowUser, CountflowRole, Guid>(options), IModuleDbContext
{
    public const string SchemaName = "identity";

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<CountflowUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(u => u.OperatorCode).HasMaxLength(32).IsRequired();
            user.HasIndex(u => u.OperatorCode).IsUnique();
        });

        builder.Entity<RefreshToken>(token =>
        {
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.UserId);

            token.HasOne<CountflowUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
