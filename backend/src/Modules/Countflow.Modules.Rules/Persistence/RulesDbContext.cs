using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Rules.Persistence;

public sealed class RulesDbContext(DbContextOptions<RulesDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "rules";

    public DbSet<RuleSet> RuleSets => Set<RuleSet>();

    public DbSet<RuleDefinition> RuleDefinitions => Set<RuleDefinition>();

    /// <summary>Decisions, kept. See <see cref="DecisionTrace"/>.</summary>
    public DbSet<DecisionTrace> DecisionTraces => Set<DecisionTrace>();

    public DbSet<DecisionTraceCandidate> DecisionTraceCandidates => Set<DecisionTraceCandidate>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<RuleSet>(ruleSet =>
        {
            ruleSet.HasKey(r => r.Id);
            ruleSet.Property(r => r.Name).HasMaxLength(128).IsRequired();
            ruleSet.Property(r => r.Notes).HasMaxLength(1000);

            ruleSet.HasMany(r => r.Rules)
                .WithOne(r => r.RuleSet!)
                .HasForeignKey(r => r.RuleSetId)
                .OnDelete(DeleteBehavior.Cascade);

            // One name owns a version line. Promoting is inserting the next
            // version, never editing the live one — a trace from last week points
            // at a specific row and that row has to still say what it said.
            ruleSet.HasIndex(r => new { r.Name, r.Version }).IsUnique();

            // At most one active set per decision kind per warehouse, enforced by
            // the database rather than by whichever endpoint happens to promote.
            // Two active putaway rule sets is not a state anything downstream
            // knows how to resolve, and NULLS NOT DISTINCT is what makes the
            // warehouse-agnostic default participate in the same rule.
            ruleSet.HasIndex(r => new { r.Kind, r.WarehouseId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter($"\"Status\" = {(int)RuleSetStatus.Active}");
        });

        builder.Entity<RuleDefinition>(rule =>
        {
            rule.HasKey(r => r.Id);
            rule.Property(r => r.Code).HasMaxLength(128).IsRequired();
            rule.Property(r => r.Weight).HasPrecision(9, 4);
            rule.Property(r => r.Parameters).HasColumnType("jsonb");

            // A rule set naming the same implementation twice would evaluate it
            // twice and double its weight, which is a tuning mistake that looks
            // like a scoring bug.
            rule.HasIndex(r => new { r.RuleSetId, r.Code }).IsUnique();
        });

        builder.Entity<DecisionTrace>(trace =>
        {
            trace.HasKey(t => t.Id);
            trace.Property(t => t.RuleSetName).HasMaxLength(128).IsRequired();
            trace.Property(t => t.SelectedCandidateKey).HasMaxLength(64);
            trace.Property(t => t.UserName).HasMaxLength(256).IsRequired();
            trace.Property(t => t.InputsJson).HasColumnType("jsonb").IsRequired();

            trace.HasMany(t => t.Candidates)
                .WithOne(c => c.Trace!)
                .HasForeignKey(c => c.DecisionTraceId)
                .OnDelete(DeleteBehavior.Cascade);

            // The retention sweep's access path: traces are read by id in normal
            // use and by age when they are eventually aged out (ADR 0004 warns
            // this table is the one that grows without bound).
            trace.HasIndex(t => t.DecidedAt);
            trace.HasIndex(t => new { t.Kind, t.DecidedAt });
            trace.HasIndex(t => t.RuleSetId);
        });

        builder.Entity<DecisionTraceCandidate>(candidate =>
        {
            candidate.HasKey(c => c.Id);
            candidate.Property(c => c.CandidateKey).HasMaxLength(64).IsRequired();
            candidate.Property(c => c.Score).HasPrecision(9, 4);
            candidate.Property(c => c.ExcludedByJson).HasColumnType("jsonb");
            candidate.Property(c => c.ScoresJson).HasColumnType("jsonb").IsRequired();

            candidate.HasIndex(c => new { c.DecisionTraceId, c.Rank });
        });

        // No foreign keys to masterdata.Locations or inventory.StockMovements, by
        // design (ADR 0002). A trace names a candidate by id and by the code that
        // was printed on the rack at the time — and the second one is what still
        // works after the location is renumbered.
    }
}
