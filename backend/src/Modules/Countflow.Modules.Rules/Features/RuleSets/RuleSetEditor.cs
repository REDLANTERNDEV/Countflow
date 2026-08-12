using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Rules.Features.RuleSets;

/// <summary>One rule as a supervisor supplies it.</summary>
public sealed record RuleDraft(
    string Code,
    int Order,
    decimal Weight = 0m,
    string? Parameters = null,
    bool IsEnabled = true);

/// <summary>
/// Writes rule sets.
/// <para>
/// Deferred since Phase 3, when the read surface and the preview shipped and the
/// only writer was the seeder, and forced by Phase 6: replay compares a candidate
/// rule set against the one that ran, and without a way to author a candidate the
/// machinery has nothing to say. The order the two landed in is the right one —
/// an editor that could not tell you what a change would do is the thing ADR 0004
/// says warehouses already have and do not use.
/// </para>
/// <para>
/// <b>Drafts are the only writable thing here.</b> An active set is what the floor
/// is running on and an archived one is what a trace from last quarter still points
/// at, so both are evidence rather than documents: editing either would rewrite the
/// explanation behind decisions that have already been carried out. Changing a live
/// rule means cloning it, editing the copy, replaying it, and promoting it — which
/// is four steps instead of one, and every one of them is a step somebody should be
/// taking.
/// </para>
/// </summary>
public sealed class RuleSetEditor(RulesDbContext dbContext, TimeProvider clock)
{
    /// <summary>
    /// A new draft, optionally cloned from an existing set.
    /// <para>
    /// Cloning is the expected path by a distance. A supervisor tuning a warehouse
    /// is changing one weight in a set of eleven rules that already works, and
    /// making them retype the other ten is how a rule set acquires a rule nobody
    /// meant to drop.
    /// </para>
    /// </summary>
    public async Task<Result<RuleSet>> CreateDraftAsync(
        DecisionKind kind,
        string name,
        Guid? warehouseId,
        string? notes,
        Guid? cloneFrom,
        IReadOnlyList<RuleDraft>? rules,
        CancellationToken cancellationToken)
    {
        if (!RuleVocabulary.HasVocabulary(kind))
        {
            return Result.Failure<RuleSet>(RulesErrors.NoVocabulary(kind.ToString()));
        }

        IReadOnlyList<RuleDraft> source;

        if (cloneFrom is { } sourceId)
        {
            var original = await dbContext.RuleSets
                .AsNoTracking()
                .Include(r => r.Rules)
                .SingleOrDefaultAsync(r => r.Id == sourceId, cancellationToken);

            if (original is null) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotFound);

            kind = original.Kind;

            source = rules ??
            [
                .. original.Rules
                    .OrderBy(rule => rule.Order)
                    .Select(rule => new RuleDraft(rule.Code, rule.Order, rule.Weight, rule.Parameters, rule.IsEnabled))
            ];
        }
        else
        {
            source = rules ?? [];
        }

        var draft = new RuleSet
        {
            Kind = kind,
            Name = name.Trim(),
            // Drafts do not take a version number from the sequence. A version is
            // stamped onto every trace and has to mean "what the floor was running",
            // so it is allocated at promotion — a draft that was abandoned must not
            // leave a hole in the numbering a trace could be read as pointing into.
            Version = 0,
            Status = RuleSetStatus.Draft,
            WarehouseId = warehouseId,
            Notes = notes,
            CreatedAt = clock.GetUtcNow()
        };

        var applied = Apply(draft, source);
        if (applied.IsFailure) return Result.Failure<RuleSet>(applied.Error);

        dbContext.RuleSets.Add(draft);
        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    /// <summary>
    /// Replaces a draft's rules wholesale.
    /// <para>
    /// Wholesale rather than a patch per rule, because constraint <i>order</i> is
    /// part of the meaning — it decides which reason an operator is shown — and a
    /// sequence of independent edits to an ordered list is how two supervisors
    /// working an hour apart produce a set neither of them intended.
    /// </para>
    /// </summary>
    public async Task<Result<RuleSet>> ReplaceAsync(
        Guid id,
        string? name,
        string? notes,
        IReadOnlyList<RuleDraft> rules,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.RuleSets
            .Include(r => r.Rules)
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (draft is null) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotFound);
        if (draft.Status != RuleSetStatus.Draft) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotEditable);

        if (!string.IsNullOrWhiteSpace(name)) draft.Name = name.Trim();
        if (notes is not null) draft.Notes = notes;

        dbContext.RuleDefinitions.RemoveRange(draft.Rules);
        draft.Rules.Clear();

        var applied = Apply(draft, rules);
        if (applied.IsFailure) return Result.Failure<RuleSet>(applied.Error);

        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    /// <summary>
    /// Puts a draft on the floor, and takes down whatever it replaces.
    /// <para>
    /// One active set per kind per warehouse is enforced by a partial unique index
    /// rather than by this method, which is the right way round: two supervisors
    /// promoting at the same instant would both find nothing active and both insert,
    /// and only the database can arbitrate that. What this does is make the ordinary
    /// case correct — archive first, then activate, in one transaction.
    /// </para>
    /// <para>
    /// The version is allocated here, as the next after the highest this kind and
    /// warehouse has ever used. Archived sets keep their numbers forever, because a
    /// trace from last quarter names one.
    /// </para>
    /// </summary>
    public async Task<Result<RuleSet>> PromoteAsync(Guid id, CancellationToken cancellationToken)
    {
        var draft = await dbContext.RuleSets
            .Include(r => r.Rules)
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (draft is null) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotFound);
        if (draft.Status != RuleSetStatus.Draft) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotPromotable);
        if (draft.Rules.Count == 0) return Result.Failure<RuleSet>(RulesErrors.RuleSetEmpty);

        var builds = RuleVocabulary.Builds(draft);
        if (builds.IsFailure) return Result.Failure<RuleSet>(builds.Error);

        var now = clock.GetUtcNow();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Cleared at the top of the delegate: a retry re-runs everything below,
            // and the change tracker would still be holding the entities the last
            // attempt modified.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var promoting = await dbContext.RuleSets
                .Include(r => r.Rules)
                .SingleAsync(r => r.Id == id, cancellationToken);

            var superseded = await dbContext.RuleSets
                .Where(r => r.Kind == promoting.Kind
                            && r.Status == RuleSetStatus.Active
                            && r.WarehouseId == promoting.WarehouseId)
                .ToListAsync(cancellationToken);

            foreach (var set in superseded)
            {
                set.Status = RuleSetStatus.Archived;
            }

            var highest = await dbContext.RuleSets
                .Where(r => r.Kind == promoting.Kind && r.WarehouseId == promoting.WarehouseId)
                .MaxAsync(r => (int?)r.Version, cancellationToken) ?? 0;

            promoting.Version = highest + 1;
            promoting.Status = RuleSetStatus.Active;
            promoting.ActivatedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        dbContext.ChangeTracker.Clear();

        return await dbContext.RuleSets
            .AsNoTracking()
            .Include(r => r.Rules)
            .SingleAsync(r => r.Id == id, cancellationToken);
    }

    /// <summary>
    /// Takes a set out of use without deleting it.
    /// <para>
    /// Never a delete, for a set in any state. An archived set is what every trace
    /// it ever produced points at, and a trace whose rule set has been removed says
    /// "decided by rules that no longer exist", which is not an explanation.
    /// </para>
    /// </summary>
    public async Task<Result<RuleSet>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var ruleSet = await dbContext.RuleSets
            .Include(r => r.Rules)
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (ruleSet is null) return Result.Failure<RuleSet>(RulesErrors.RuleSetNotFound);

        ruleSet.Status = RuleSetStatus.Archived;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ruleSet;
    }

    /// <summary>
    /// Writes the rules onto the set, refusing anything that could not run.
    /// <para>
    /// Three checks the catalogue cannot make, then the catalogue itself. It cannot
    /// see a duplicate — it would build two copies of the same rule quite happily and
    /// double its weight — and it has no opinion on a weight of 40, which would
    /// silently outvote every other rule in the set while looking like a typo,
    /// because it is one.
    /// </para>
    /// </summary>
    private static Result Apply(RuleSet ruleSet, IReadOnlyList<RuleDraft> rules)
    {
        if (rules.Count == 0)
        {
            return Result.Failure(RulesErrors.RuleSetEmpty);
        }

        var constraints = RuleVocabulary.ConstraintCodes(ruleSet.Kind);
        var preferences = RuleVocabulary.PreferenceCodes(ruleSet.Kind);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            var code = rule.Code.Trim();

            if (!seen.Add(code))
            {
                return Result.Failure(RulesErrors.DuplicateRule(code));
            }

            var isConstraint = constraints.Contains(code, StringComparer.Ordinal);
            var isPreference = preferences.Contains(code, StringComparer.Ordinal);

            if (!isConstraint && !isPreference)
            {
                return Result.Failure(RulesErrors.RuleNotInVocabulary(code, ruleSet.Kind.ToString()));
            }

            if (isPreference && (rule.Weight < 0m || rule.Weight > 1m))
            {
                return Result.Failure(RulesErrors.WeightOutOfRange(code));
            }

            ruleSet.Rules.Add(new RuleDefinition
            {
                RuleSetId = ruleSet.Id,
                Code = code,
                Role = isConstraint ? RuleRole.Constraint : RuleRole.Preference,
                Order = rule.Order,
                // A constraint cannot be outvoted, so a weight on one is meaningless
                // rather than harmless: it would show up in the editor as a number
                // somebody could tune and nothing would change.
                Weight = isConstraint ? 0m : rule.Weight,
                Parameters = string.IsNullOrWhiteSpace(rule.Parameters) ? null : rule.Parameters,
                IsEnabled = rule.IsEnabled
            });
        }

        // And finally the real thing: would this build? Parameters are checked here
        // and nowhere else, because only the rule that reads them knows their shape.
        return RuleVocabulary.Builds(ruleSet);
    }
}
