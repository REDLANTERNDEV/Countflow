using Countflow.Modules.Labels.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Persistence;

public sealed class LabelsDbContext(DbContextOptions<LabelsDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "labels";

    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();

    public DbSet<LabelElement> LabelElements => Set<LabelElement>();

    public DbSet<LabelPrint> LabelPrints => Set<LabelPrint>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<LabelTemplate>(template =>
        {
            template.HasKey(t => t.Id);
            template.Property(t => t.Name).HasMaxLength(200).IsRequired();
            template.Property(t => t.Notes).HasMaxLength(1000);

            template.HasMany(t => t.Elements)
                .WithOne(e => e.Template!)
                .HasForeignKey(e => e.LabelTemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            // One active template per kind per site, arbitrated by the database
            // rather than by the promotion code. Two supervisors promoting at the
            // same instant would both find nothing active and both write, and only
            // a unique index can settle that. Two partial indexes because Postgres
            // treats NULLs as distinct, so the site-specific rule and the
            // "every warehouse" rule need separate guards.
            template.HasIndex(t => new { t.Kind, t.WarehouseId })
                .IsUnique()
                .HasFilter($"\"Status\" = {(int)LabelTemplateStatus.Active} AND \"WarehouseId\" IS NOT NULL");

            template.HasIndex(t => t.Kind)
                .IsUnique()
                .HasFilter($"\"Status\" = {(int)LabelTemplateStatus.Active} AND \"WarehouseId\" IS NULL");

            template.HasIndex(t => new { t.Kind, t.Status });
        });

        builder.Entity<LabelElement>(element =>
        {
            element.HasKey(e => e.Id);
            element.Property(e => e.FieldCode).HasMaxLength(64);
            element.Property(e => e.Literal).HasMaxLength(200);
            element.Property(e => e.Rotation).HasMaxLength(1).IsRequired();

            element.HasIndex(e => new { e.LabelTemplateId, e.Order });
        });

        builder.Entity<LabelPrint>(print =>
        {
            print.HasKey(p => p.Id);
            print.Property(p => p.TemplateName).HasMaxLength(200).IsRequired();
            print.Property(p => p.SubjectKey).HasMaxLength(64).IsRequired();
            print.Property(p => p.UserName).HasMaxLength(256).IsRequired();
            print.Property(p => p.PayloadHash).HasMaxLength(64).IsRequired();
            print.Property(p => p.IdempotencyKey).HasMaxLength(256).IsRequired();

            // A retried print converges on the label it already made rather than
            // recording a second one. A handheld's outbox retries, and a retry
            // counted as a reprint would accuse an operator of printing a duplicate
            // they never asked for.
            print.HasIndex(p => p.IdempotencyKey).IsUnique();

            // The audit question, and the reason this table exists: everything ever
            // printed for one pallet, newest first.
            print.HasIndex(p => new { p.SubjectKey, p.PrintedAt });

            print.HasIndex(p => new { p.WarehouseId, p.PrintedAt });

            print.HasIndex(p => p.LabelTemplateId);
        });

        // No foreign key from a print to its template, deliberately. The three
        // stamped columns are the record — id, name and version, copied the way a
        // decision trace copies its rule set's (ADR 0004) — and a print has to go
        // on saying what produced it whatever becomes of the row it names.
    }
}
