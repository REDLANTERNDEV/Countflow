using Countflow.Modules.Replay.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Replay.Persistence;

public sealed class ReplayDbContext(DbContextOptions<ReplayDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "replay";

    public DbSet<ReplayRun> ReplayRuns => Set<ReplayRun>();

    public DbSet<ReplayRunDecision> ReplayRunDecisions => Set<ReplayRunDecision>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<ReplayRun>(run =>
        {
            run.HasKey(r => r.Id);
            run.Property(r => r.Reference).HasMaxLength(64).IsRequired();
            run.Property(r => r.CandidateRuleSetName).HasMaxLength(200).IsRequired();
            run.Property(r => r.BaselineRuleSetName).HasMaxLength(200).IsRequired();
            run.Property(r => r.RequestedByUserName).HasMaxLength(256).IsRequired();
            run.Property(r => r.Notes).HasMaxLength(1000);

            // jsonb rather than text, for the reason the trace blobs are: it is the
            // right type for the data. Note the consequence, which has bitten before
            // — Postgres has no LIKE for jsonb, so nothing may filter on this column
            // with .Contains(); anything that needs to search it filters in memory.
            run.Property(r => r.InfluenceJson).HasColumnType("jsonb").IsRequired();

            run.HasMany(r => r.Decisions)
                .WithOne(d => d.Run!)
                .HasForeignKey(d => d.ReplayRunId)
                .OnDelete(DeleteBehavior.Cascade);

            run.HasIndex(r => new { r.WarehouseId, r.Reference }).IsUnique();
            run.HasIndex(r => new { r.WarehouseId, r.Kind, r.CreatedAt });
            run.HasIndex(r => r.CandidateRuleSetId);

            run.ToTable(table =>
            {
                // A run cannot have reproduced more decisions than it replayed. The
                // credibility figure is the one number somebody will read without
                // reading the rest, and a check is cheap insurance against it being
                // arithmetic nobody looked at.
                table.HasCheckConstraint(
                    "CK_ReplayRuns_FidelitySumsToReplayed",
                    "\"ExactCount\" + \"ApproximateCount\" + \"UnreplayableCount\" = \"ReplayedCount\"");

                table.HasCheckConstraint("CK_ReplayRuns_Window", "\"FromDate\" <= \"ToDate\"");
            });
        });

        builder.Entity<ReplayRunDecision>(decision =>
        {
            decision.HasKey(d => d.Id);
            decision.Property(d => d.Subject).HasMaxLength(200).IsRequired();
            decision.Property(d => d.FidelityNote).HasMaxLength(100);
            decision.Property(d => d.RecordedKey).HasMaxLength(256);
            decision.Property(d => d.BaselineKey).HasMaxLength(256);
            decision.Property(d => d.CandidateKey).HasMaxLength(256);
            decision.Property(d => d.BaselineScore).HasPrecision(9, 6);
            decision.Property(d => d.CandidateScore).HasPrecision(9, 6);
            decision.Property(d => d.BaselineInfluenceJson).HasColumnType("jsonb").IsRequired();
            decision.Property(d => d.CandidateInfluenceJson).HasColumnType("jsonb").IsRequired();

            // The console's first question of a run is "show me what changed", and
            // on a two-hundred-decision run that is four rows out of two hundred.
            decision.HasIndex(d => new { d.ReplayRunId, d.Outcome });
            decision.HasIndex(d => d.DecisionTraceId);
        });

        // No foreign keys to rules.DecisionTraces or rules.RuleSets, by design
        // (ADR 0002). A run names both by id and asks their owner about them, which
        // is what keeps this module a record of experiments rather than a second
        // rules engine.
    }
}
