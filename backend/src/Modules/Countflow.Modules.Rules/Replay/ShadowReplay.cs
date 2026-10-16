using System.Diagnostics;
using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.CycleCount;
using Countflow.Modules.Rules.Dispositions;
using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Features.RuleSets;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Rules.Replay;

/// <summary>
/// Runs a candidate rule set against decisions the warehouse actually made
/// (ADR 0004).
/// <para>
/// The third product pillar, and the one the other two exist to make possible. It
/// needs both of them and would be impossible without either: the ledger, because
/// the state a decision saw has to be rebuildable rather than remembered
/// (ADR 0001); and the engine, because a rule set has to be a thing that can be
/// handed different inputs and run again, which a scattered <c>if</c> is not.
/// </para>
/// <para>
/// <b>Both sides are re-run.</b> The obvious implementation compares the candidate
/// rules against what the trace records, and it is wrong in a way that would have
/// been discovered late: <c>TraceRetention</c> keeps thirty exclusions sampled
/// round-robin across reasons, so a stored trace cannot say where an arbitrary
/// candidate ranked, and a diff built from two of them would be reporting
/// differences in what the sampler happened to keep. Running the original rules
/// again over the same rebuilt warehouse costs one more evaluation — measured in
/// microseconds, on traces that record their own — and buys two things worth far
/// more: a full ranking on both sides, and a check on the reconstruction itself.
/// </para>
/// <para>
/// That check is the honesty mechanism. If the rules that made a decision, given
/// the warehouse as rebuilt, choose what they are recorded as having chosen, the
/// reconstruction is good enough to have produced the original answer and the
/// comparison means what it appears to. If they do not, the decision is reported
/// <c>Approximate</c> and counted separately. Which decision kinds are exactly
/// replayable is therefore a measurement rather than a claim — which is just as
/// well, because the claim in ADR 0001 turned out to be wrong in Phase 6's favour.
/// </para>
/// </summary>
public sealed class ShadowReplay(
    RulesDbContext dbContext,
    IItemDirectory items,
    ILocationDirectory locations,
    IStockQuery stock,
    IStockHistory history,
    ILogger<ShadowReplay> logger) : IShadowReplay
{
    /// <summary>
    /// A rebuilt decision, reduced to what a comparison needs. Erasing the candidate
    /// type here is what lets one comparison serve three vocabularies that
    /// deliberately share nothing else.
    /// </summary>
    private sealed record Rerun(
        string? WinnerKey,
        decimal WinnerScore,
        int ConsideredCount,
        int AdmissibleCount,
        IReadOnlyList<InfluenceLine> Influence,
        IReadOnlyDictionary<string, int> RankByKey,
        long ElapsedMicroseconds);

    public async Task<Result<ReplayResult>> RunAsync(
        ReplayScope scope,
        Guid candidateRuleSetId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        var candidateSet = await dbContext.RuleSets
            .AsNoTracking()
            .Include(set => set.Rules)
            .SingleOrDefaultAsync(set => set.Id == candidateRuleSetId, cancellationToken);

        if (candidateSet is null) return Result.Failure<ReplayResult>(RulesErrors.RuleSetNotFound);

        if (candidateSet.Kind != scope.Kind)
        {
            return Result.Failure<ReplayResult>(
                RulesErrors.RuleSetKindMismatch(candidateSet.Kind.ToString(), scope.Kind.ToString()));
        }

        var builds = RuleVocabulary.Builds(candidateSet);
        if (builds.IsFailure) return Result.Failure<ReplayResult>(builds.Error);

        var subjects = await FindTracesAsync(scope, cancellationToken);

        var storage = await locations.FindStorageCandidatesAsync(scope.WarehouseId, cancellationToken);
        if (storage.Count == 0) return Result.Failure<ReplayResult>(RulesErrors.LocationNotFound);

        var storageById = storage.ToDictionary(location => location.Id);

        // One scale for the whole run: the building does not move between decisions,
        // and rebuilding it per decision would be the same answer computed two
        // hundred times.
        var route = RouteScale.Of(storage);

        var lookups = new ReplayLookups(items, stock);
        var ruleSets = new Dictionary<Guid, RuleSet?>();
        var decisions = new List<ReplayedDecision>(subjects.Count);

        if (subjects.Count > 0)
        {
            var index = 0;

            await foreach (var snapshot in history.WalkAsync(
                [.. storageById.Keys],
                [.. subjects.Select(subject => subject.Trace.DecidedAt)],
                cancellationToken))
            {
                var subject = subjects[index++];

                decisions.Add(await ReplayOneAsync(
                    subject, snapshot, candidateSet, storage, storageById, route, lookups, ruleSets,
                    cancellationToken));
            }
        }

        var baseline = subjects.Count == 0
            ? new RuleSetStamp(Guid.Empty, string.Empty, 0)
            : new RuleSetStamp(
                subjects[0].Trace.RuleSetId,
                subjects[0].Trace.RuleSetName,
                subjects[0].Trace.RuleSetVersion);

        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var result = new ReplayResult(
            baseline,
            new RuleSetStamp(candidateSet.Id, candidateSet.Name, candidateSet.Version),
            scope.Kind,
            decisions.Count,
            decisions.Count(d => d.Fidelity == ReplayFidelity.Exact),
            decisions.Count(d => d.Fidelity == ReplayFidelity.Approximate),
            decisions.Count(d => d.Fidelity == ReplayFidelity.Unreplayable),
            decisions.Count(d => d.Outcome == ReplayOutcome.Changed),
            decisions.Count(d => d.Outcome == ReplayOutcome.Reordered),
            decisions.Count(d => d.Outcome == ReplayOutcome.Gained),
            decisions.Count(d => d.Outcome == ReplayOutcome.Lost),
            decisions,
            Aggregate(decisions),
            elapsed);

        logger.LogInformation(
            "Replayed {Count} {Kind} decisions under {Candidate} v{Version}: {Changed} changed, {Reordered} " +
            "reordered, {Lost} lost, {Exact} reproduced exactly, in {Elapsed}ms",
            result.ReplayedCount,
            scope.Kind,
            candidateSet.Name,
            candidateSet.Version,
            result.ChangedCount,
            result.ReorderedCount,
            result.LostCount,
            result.ExactCount,
            elapsed);

        return result;
    }

    /// <summary>
    /// The decisions in the window, narrowed to one warehouse.
    /// <para>
    /// The narrowing happens in memory, and not for want of trying: the warehouse is
    /// a field of <c>InputsJson</c>, which is <c>jsonb</c>, and Postgres has no
    /// <c>LIKE</c> for jsonb — a predicate over it fails at runtime with "operator
    /// does not exist". The window is what bounds the read, so a caller asking for a
    /// month gets a month's traces rather than the archive.
    /// </para>
    /// <para>
    /// Previews are excluded. A supervisor asking "where would you put this?" is a
    /// question rather than a decision, and replaying questions would let somebody's
    /// afternoon of experimenting outnumber the warehouse's real work.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Subject>> FindTracesAsync(
        ReplayScope scope,
        CancellationToken cancellationToken)
    {
        var traces = await dbContext.DecisionTraces
            .AsNoTracking()
            .Where(trace => trace.Kind == scope.Kind
                            && !trace.IsPreview
                            && trace.DecidedAt >= scope.From
                            && trace.DecidedAt <= scope.To)
            .OrderBy(trace => trace.DecidedAt)
            .ThenBy(trace => trace.Id)
            .ToListAsync(cancellationToken);

        var subjects = new List<Subject>();

        foreach (var trace in traces)
        {
            if (subjects.Count >= Math.Clamp(scope.Limit, 1, 1000))
            {
                break;
            }

            var inputs = Parse(scope.Kind, trace.InputsJson);

            if (inputs is not null && WarehouseOf(inputs) == scope.WarehouseId)
            {
                subjects.Add(new Subject(trace, inputs));
            }
        }

        return subjects;
    }

    private async Task<ReplayedDecision> ReplayOneAsync(
        Subject subject,
        WarehouseAsOf snapshot,
        RuleSet candidateSet,
        IReadOnlyList<StorageLocationSnapshot> storage,
        IReadOnlyDictionary<Guid, StorageLocationSnapshot> storageById,
        RouteScale route,
        ReplayLookups lookups,
        Dictionary<Guid, RuleSet?> ruleSets,
        CancellationToken cancellationToken)
    {
        var trace = subject.Trace;

        var baselineSet = await LoadAsync(trace.RuleSetId, ruleSets, cancellationToken);

        if (baselineSet is null)
        {
            return Unreplayable(subject, "replay.rule_set_gone");
        }

        var rebuilt = subject.Inputs switch
        {
            PutawayInputs putaway => await RebuildPutawayAsync(
                putaway, snapshot, storage, route, lookups, baselineSet, candidateSet, cancellationToken),

            AllocationInputs allocation => await RebuildAllocationAsync(
                allocation, snapshot, storageById, lookups, baselineSet, candidateSet, cancellationToken),

            CountPolicyInputs counting => await RebuildCountAsync(
                counting, snapshot, storage, storageById, route, lookups, baselineSet, candidateSet,
                cancellationToken),

            // No async work and no warehouse state, which is why it does not take
            // the snapshot the other three do. Every input to a disposition is
            // either on the trace or on the item, and the candidates are four
            // constants — so this is the only one of the four that would replay
            // correctly with the database switched off.
            DispositionInputs disposition => await RebuildDispositionAsync(
                disposition, lookups, baselineSet, candidateSet, cancellationToken),

            _ => Result.Failure<Comparison>(RulesErrors.NoVocabulary(trace.Kind.ToString()))
        };

        if (rebuilt.IsFailure)
        {
            return Unreplayable(subject, rebuilt.Error.Code);
        }

        var (baseline, candidate, note) = rebuilt.Value;

        // The reconstruction checks itself. Both null — no admissible candidate then,
        // none now — counts as agreement, because "nowhere to put it" is a real
        // answer that a replay has to be able to reproduce.
        var reproduced = string.Equals(baseline.WinnerKey, trace.SelectedCandidateKey, StringComparison.Ordinal);

        return new ReplayedDecision(
            trace.Id,
            trace.DecidedAt,
            Describe(subject.Inputs),
            reproduced ? ReplayFidelity.Exact : ReplayFidelity.Approximate,
            reproduced ? note : note ?? "replay.baseline_diverged",
            Compare(baseline, candidate),
            trace.SelectedCandidateKey,
            baseline.WinnerKey,
            candidate.WinnerKey,
            baseline.WinnerScore,
            candidate.WinnerScore,
            RankOf(baseline, candidate.WinnerKey),
            RankOf(candidate, baseline.WinnerKey),
            baseline.ConsideredCount,
            baseline.AdmissibleCount,
            candidate.AdmissibleCount,
            baseline.Influence,
            candidate.Influence,
            baseline.ElapsedMicroseconds + candidate.ElapsedMicroseconds);
    }

    // ------------------------------------------------------------- rebuilding

    /// <summary>
    /// Putaway, rebuilt. The exactly-replayable case: the candidates are the
    /// building, the context is occupancy, and occupancy as of an instant is
    /// precisely what summing the ledger to that instant produces.
    /// </summary>
    private async Task<Result<Comparison>> RebuildPutawayAsync(
        PutawayInputs inputs,
        WarehouseAsOf snapshot,
        IReadOnlyList<StorageLocationSnapshot> storage,
        RouteScale route,
        ReplayLookups lookups,
        RuleSet baselineSet,
        RuleSet candidateSet,
        CancellationToken cancellationToken)
    {
        if (inputs.PalletContents is { } contents)
            return await RebuildPalletPutawayAsync(inputs, contents, snapshot, storage,
                route, lookups, baselineSet, candidateSet, cancellationToken);

        var itemsById = await lookups.ItemsAsync(
            snapshot.Holdings.Select(h => h.ItemId).Append(inputs.ItemId), cancellationToken);

        if (!itemsById.TryGetValue(inputs.ItemId, out var item))
        {
            return Result.Failure<Comparison>(RulesErrors.ItemNotFound);
        }

        var occupancy = LocationOccupancy.Summarise(
            snapshot.Holdings
                .Where(holding => holding.Quantity > 0m)
                .Select(holding => new OccupancyLine(
                    holding.LocationId, holding.ItemId, holding.LotId, holding.Quantity)),
            itemsById,
            inputs.ItemId,
            inputs.LotId);

        var context = new PutawayContext(
            item,
            inputs.LotId,
            inputs.Quantity,
            inputs.Condition,
            inputs.FromLocationId,
            occupancy,
            RequiredVolumeM3: item.UnitVolumeM3 * inputs.Quantity,
            RequiredWeightKg: item.UnitWeightKg * inputs.Quantity,
            Route: route);

        var baseline = PutawayRuleCatalogue.Build(baselineSet);
        if (baseline.IsFailure) return Result.Failure<Comparison>(baseline.Error);

        var candidate = PutawayRuleCatalogue.Build(candidateSet);
        if (candidate.IsFailure) return Result.Failure<Comparison>(candidate.Error);

        return new Comparison(
            Run(DecisionKind.Putaway, baseline.Value.Stamp, storage, context,
                baseline.Value.Constraints, baseline.Value.Preferences),
            Run(DecisionKind.Putaway, candidate.Value.Stamp, storage, context,
                candidate.Value.Constraints, candidate.Value.Preferences),
            null);
    }

    private async Task<Result<Comparison>> RebuildPalletPutawayAsync(
        PutawayInputs inputs,
        IReadOnlyList<PlateLine> contents,
        WarehouseAsOf snapshot,
        IReadOnlyList<StorageLocationSnapshot> storage,
        RouteScale route,
        ReplayLookups lookups,
        RuleSet baselineSet,
        RuleSet candidateSet,
        CancellationToken cancellationToken)
    {
        var itemsById = await lookups.ItemsAsync(snapshot.Holdings.Select(holding => holding.ItemId)
            .Concat(contents.Select(line => line.ItemId)), cancellationToken);
        var holdings = snapshot.Holdings.Where(holding => holding.Quantity > 0m)
            .Select(holding => new OccupancyLine(holding.LocationId, holding.ItemId, holding.LotId, holding.Quantity))
            .ToArray();
        var occupancy = contents.Select(line => new StockSubject(line.ItemId, line.LotId)).Distinct()
            .ToDictionary(subject => subject, subject => LocationOccupancy.Summarise(
                holdings, itemsById, subject.ItemId, subject.LotId));
        var context = PalletPutawayContext.Create(contents, inputs.FromLocationId, itemsById, occupancy, route);
        if (context.IsFailure) return Result.Failure<Comparison>(context.Error);
        var baseline = PalletPutawayRuleCatalogue.Build(baselineSet);
        if (baseline.IsFailure) return Result.Failure<Comparison>(baseline.Error);
        var candidate = PalletPutawayRuleCatalogue.Build(candidateSet);
        if (candidate.IsFailure) return Result.Failure<Comparison>(candidate.Error);
        return new Comparison(
            Run(DecisionKind.Putaway, baseline.Value.Stamp, storage, context.Value,
                baseline.Value.Constraints, baseline.Value.Preferences),
            Run(DecisionKind.Putaway, candidate.Value.Stamp, storage, context.Value,
                candidate.Value.Constraints, candidate.Value.Preferences), null);
    }

    /// <summary>
    /// Allocation, rebuilt — including the commitments standing at the time.
    /// <para>
    /// Phase 4 recorded that reservations were the one input a replay could not
    /// reconstruct, because they are current state rather than history. Phase 6 went
    /// to write the caveat and found it was not true: a claim carries
    /// <c>CreatedAt</c> and a set-once <c>ClosedAt</c>, its quantity is immutable and
    /// its row is never deleted, so the claims open at an instant come straight out
    /// of the table. Available-to-promise as of last month is therefore recoverable
    /// exactly, which makes allocation as replayable as putaway — and the baseline
    /// reproduction is what proves it per decision rather than in a paragraph.
    /// </para>
    /// </summary>
    private async Task<Result<Comparison>> RebuildAllocationAsync(
        AllocationInputs inputs,
        WarehouseAsOf snapshot,
        IReadOnlyDictionary<Guid, StorageLocationSnapshot> storageById,
        ReplayLookups lookups,
        RuleSet baselineSet,
        RuleSet candidateSet,
        CancellationToken cancellationToken)
    {
        var itemsById = await lookups.ItemsAsync([inputs.ItemId], cancellationToken);

        if (!itemsById.TryGetValue(inputs.ItemId, out var item))
        {
            return Result.Failure<Comparison>(RulesErrors.ItemNotFound);
        }

        var held = snapshot.Holdings
            .Where(holding => holding.ItemId == inputs.ItemId && holding.Quantity > 0m)
            .ToList();

        var lots = await lookups.LotsAsync(
            held.Where(h => h.LotId is not null).Select(h => h.LotId!.Value), cancellationToken);

        var plates = await lookups.PlatesAsync(
            held.Where(h => h.LicensePlateId is not null).Select(h => h.LicensePlateId!.Value), cancellationToken);

        var candidates = held
            .Select(holding =>
            {
                var lot = holding.LotId is { } lotId ? lots.GetValueOrDefault(lotId) : null;

                return new StockHolding(
                    holding.BalanceId,
                    holding.ItemId,
                    holding.LotId,
                    lot?.LotNumber,
                    lot?.ExpiryDate,
                    holding.LocationId,
                    holding.LicensePlateId,
                    holding.LicensePlateId is { } plateId ? plates.GetValueOrDefault(plateId) : null,
                    holding.Condition,
                    holding.Quantity,
                    snapshot.ReservedByBalance.GetValueOrDefault(holding.BalanceId))
                {
                    LocationCode = storageById.GetValueOrDefault(holding.LocationId)?.Code ?? string.Empty,
                    FirstReceivedAt = holding.FirstReceivedAt
                };
            })
            .ToList();

        var locationsById = candidates
            .Select(holding => storageById.GetValueOrDefault(holding.LocationId))
            .Where(location => location is not null)
            .DistinctBy(location => location!.Id)
            .ToDictionary(location => location!.Id, location => location!);

        var expiries = candidates
            .Where(holding => holding.ExpiryDate is not null)
            .Select(holding => holding.ExpiryDate!.Value)
            .ToList();

        var positions = locationsById.Values
            .Select(location => ((long)location.ZonePickSequence * 1_000_000) + location.PickSequence)
            .ToList();
        var receipts = candidates.Where(holding => holding.FirstReceivedAt is not null).Select(holding => holding.FirstReceivedAt!.Value).ToList();

        var context = new AllocationContext(
            item,
            inputs.Quantity,
            inputs.RequiredLotId,
            inputs.ShipOn,
            locationsById,
            expiries.Count == 0 ? null : expiries.Min(),
            expiries.Count == 0 ? null : expiries.Max(),
            positions.Count == 0 ? 0L : positions.Min(),
            positions.Count == 0 ? 0L : positions.Max())
        {
            EarliestReceipt = receipts.Count == 0 ? null : receipts.Min(),
            LatestReceipt = receipts.Count == 0 ? null : receipts.Max()
        };

        var baseline = AllocationRuleCatalogue.Build(baselineSet);
        if (baseline.IsFailure) return Result.Failure<Comparison>(baseline.Error);

        var candidate = AllocationRuleCatalogue.Build(candidateSet);
        if (candidate.IsFailure) return Result.Failure<Comparison>(candidate.Error);

        return new Comparison(
            Run(DecisionKind.Allocation, baseline.Value.Stamp, candidates, context,
                AllocationRuleCatalogue.RequireSourceRole(baseline.Value, inputs.RequiredSourceRole).Constraints, baseline.Value.Preferences),
            Run(DecisionKind.Allocation, candidate.Value.Stamp, candidates, context,
                AllocationRuleCatalogue.RequireSourceRole(candidate.Value, inputs.RequiredSourceRole).Constraints, candidate.Value.Preferences),
            null);
    }

    /// <summary>
    /// A counting policy, rebuilt. Two append-only histories and a clock supply
    /// almost all of it — which is why counts are the natural ground truth for a
    /// replay, and why this is the kind with the fewest excuses available to it.
    /// <para>
    /// The one hole is the holdings that were already on somebody's list, which the
    /// trace records as a count rather than a set (see <see cref="CountPolicyInputs"/>).
    /// When that count is zero the reconstruction is complete; when it is not, the
    /// exclusion is replayed as empty and the decision is noted, because a policy
    /// that would have skipped four bays is being asked about all of them.
    /// </para>
    /// </summary>
    private async Task<Result<Comparison>> RebuildCountAsync(
        CountPolicyInputs inputs,
        WarehouseAsOf snapshot,
        IReadOnlyList<StorageLocationSnapshot> storage,
        IReadOnlyDictionary<Guid, StorageLocationSnapshot> storageById,
        RouteScale route,
        ReplayLookups lookups,
        RuleSet baselineSet,
        RuleSet candidateSet,
        CancellationToken cancellationToken)
    {
        var held = snapshot.Holdings.Where(holding => holding.Quantity != 0m).ToList();

        var itemsById = await lookups.ItemsAsync(held.Select(h => h.ItemId), cancellationToken);

        var lots = await lookups.LotsAsync(
            held.Where(h => h.LotId is not null).Select(h => h.LotId!.Value), cancellationToken);

        var candidates = held
            .Select(holding =>
            {
                var lot = holding.LotId is { } lotId ? lots.GetValueOrDefault(lotId) : null;
                var counted = snapshot.CountedByBalance.GetValueOrDefault(holding.BalanceId);

                return new CountCandidate(
                    holding.BalanceId,
                    holding.ItemId,
                    holding.LotId,
                    lot?.LotNumber,
                    lot?.ExpiryDate,
                    holding.LocationId,
                    holding.LicensePlateId,
                    holding.Condition,
                    holding.Quantity,
                    counted?.CountedAt,
                    holding.LastMovementSequence,
                    counted?.AtMovementSequence,
                    holding.LastMovedAt)
                {
                    LocationCode = storageById.GetValueOrDefault(holding.LocationId)?.Code ?? string.Empty
                };
            })
            .ToList();

        var field = candidates
            .Select(candidate => storageById.GetValueOrDefault(candidate.LocationId))
            .Where(location => location is not null)
            .Select(location => location!);

        var context = new CountContext(
            // The clock the decision ran on, not the clock now. Every preference in
            // this vocabulary measures an age, so replaying tonight's policy against
            // last month's warehouse under today's date would report every holding as
            // a month more overdue than it was and agree with nothing.
            inputs.EvaluatedAt,
            itemsById,
            storage.ToDictionary(location => location.Id),
            new HashSet<Guid>(),
            candidates
                .GroupBy(candidate => candidate.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(c => c.OnHandQuantity)),
            route,
            RouteScale.Of(field));

        var baseline = CountRuleCatalogue.Build(baselineSet);
        if (baseline.IsFailure) return Result.Failure<Comparison>(baseline.Error);

        var candidate = CountRuleCatalogue.Build(candidateSet);
        if (candidate.IsFailure) return Result.Failure<Comparison>(candidate.Error);

        return new Comparison(
            Run(DecisionKind.CycleCount, baseline.Value.Stamp, candidates, context,
                baseline.Value.Constraints, baseline.Value.Preferences),
            Run(DecisionKind.CycleCount, candidate.Value.Stamp, candidates, context,
                candidate.Value.Constraints, candidate.Value.Preferences),
            inputs.AlreadyScheduledCount > 0 ? "replay.scheduled_holdings_unknown" : null);
    }

    /// <summary>
    /// Rebuilds a disposition, which is the cheapest of the four by a wide margin.
    /// <para>
    /// The candidates are <see cref="DispositionOption.All"/> — four constants — and
    /// every input the rules score on was written onto the trace when the decision
    /// ran, because none of it is derivable from the ledger. An inspector's report
    /// that a carton was sealed is a primary fact with no history behind it to
    /// re-derive, which is exactly why <see cref="DispositionInputs"/> is the fattest
    /// of the four input records and this method is the thinnest of the four
    /// rebuilds.
    /// </para>
    /// <para>
    /// The one thing it cannot reproduce is the item. Master data is mutable and not
    /// versioned, so an item whose hazardous flag or shelf life has changed since the
    /// decision replays against today's values, and the note says so — the same
    /// honesty the other three rebuilds apply to their own soft spots.
    /// </para>
    /// </summary>
    private static async Task<Result<Comparison>> RebuildDispositionAsync(
        DispositionInputs inputs,
        ReplayLookups lookups,
        RuleSet baselineSet,
        RuleSet candidateSet,
        CancellationToken cancellationToken)
    {
        var itemsById = await lookups.ItemsAsync([inputs.ItemId], cancellationToken);

        if (itemsById.GetValueOrDefault(inputs.ItemId) is not { } item)
        {
            return Result.Failure<Comparison>(RulesErrors.ItemNotFound);
        }

        var context = new DispositionContext(
            item,
            inputs.LotId,
            inputs.Quantity,
            inputs.AtLocationId,
            new InspectionFindings
            {
                Condition = inputs.FoundCondition,
                IsSealed = inputs.IsSealed,
                IsRepairable = inputs.IsRepairable
            },

            // Read off the trace rather than looked up now. Whether the warehouse
            // has a repair vendor is present state, not history, so a replay that
            // asked the directory would silently re-admit a candidate the original
            // decision excluded and report a change that never happened.
            inputs.RepairRouteExists,
            inputs.DaysSinceDespatch,
            DispositionAdvisor.RemainingShelfLife(
                item,
                inputs.ExpiryDate,
                // The date the decision ran on, not today. Shelf life is measured
                // forward from the moment somebody asked, and replaying last
                // quarter's returns under this morning's date would report every
                // batch as a quarter closer to expiry than it was.
                DateOnly.FromDateTime(inputs.EvaluatedAt.UtcDateTime)));

        var baseline = DispositionRuleCatalogue.Build(baselineSet);
        if (baseline.IsFailure) return Result.Failure<Comparison>(baseline.Error);

        var candidate = DispositionRuleCatalogue.Build(candidateSet);
        if (candidate.IsFailure) return Result.Failure<Comparison>(candidate.Error);

        return new Comparison(
            Run(DecisionKind.Disposition, baseline.Value.Stamp, DispositionOption.All, context,
                baseline.Value.Constraints, baseline.Value.Preferences),
            Run(DecisionKind.Disposition, candidate.Value.Stamp, DispositionOption.All, context,
                candidate.Value.Constraints, candidate.Value.Preferences),
            "replay.item_master_data_current");
    }

    // ---------------------------------------------------------- the machinery

    /// <summary>
    /// One evaluation, reduced to a comparable shape. The only place the four
    /// vocabularies meet, and it stays generic in both the candidate and the context
    /// so that meeting costs them nothing.
    /// </summary>
    private static Rerun Run<TCandidate, TContext>(
        DecisionKind kind,
        RuleSetStamp stamp,
        IReadOnlyList<TCandidate> candidates,
        TContext context,
        IReadOnlyList<IConstraintRule<TCandidate, TContext>> constraints,
        IReadOnlyList<IPreferenceRule<TCandidate, TContext>> preferences)
        where TCandidate : IRuleCandidate
    {
        var decision = RuleEngine.Evaluate(kind, stamp, candidates, context, constraints, preferences);

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var evaluation in decision.Candidates.Where(evaluation => evaluation.IsAdmissible))
        {
            ranks.TryAdd(evaluation.Candidate.Key, evaluation.Rank);
        }

        return new Rerun(
            decision.Winner?.Candidate.Key,
            decision.Winner?.Score ?? 0m,
            decision.ConsideredCount,
            decision.AdmissibleCount,
            RuleInfluence.Of(decision).Rules,
            ranks,
            (long)decision.Elapsed.TotalMicroseconds);
    }

    private static ReplayOutcome Compare(Rerun baseline, Rerun candidate)
    {
        if (baseline.WinnerKey is null && candidate.WinnerKey is null) return ReplayOutcome.Unchanged;
        if (baseline.WinnerKey is null) return ReplayOutcome.Gained;
        if (candidate.WinnerKey is null) return ReplayOutcome.Lost;

        if (!string.Equals(baseline.WinnerKey, candidate.WinnerKey, StringComparison.Ordinal))
        {
            return ReplayOutcome.Changed;
        }

        // Same winner is not the same answer. A set that agrees today because the
        // runner-up was never reached is one delivery away from disagreeing, and a
        // replay that reported only the winners would call that "no change".
        var reordered = baseline.RankByKey.Any(entry =>
            candidate.RankByKey.TryGetValue(entry.Key, out var rank) && rank != entry.Value);

        return reordered ? ReplayOutcome.Reordered : ReplayOutcome.Unchanged;
    }

    /// <summary>Zero when the key is not admissible under that set, which is itself the answer.</summary>
    private static int RankOf(Rerun rerun, string? key) =>
        key is not null && rerun.RankByKey.TryGetValue(key, out var rank) ? rank : 0;

    /// <summary>
    /// What each rule was worth across the whole run.
    /// <para>
    /// The report a single decision cannot give. One putaway where travel did the
    /// deciding is a bay that happened to be a long way off; travel doing the
    /// deciding on nine hundred of them is a rule set that is not doing what its
    /// weights say it does — which is the shape of every scoring flaw found so far.
    /// </para>
    /// </summary>
    private static IReadOnlyList<RuleInfluenceSummary> Aggregate(IReadOnlyList<ReplayedDecision> decisions)
    {
        var lines = decisions
            .SelectMany(decision => decision.CandidateInfluence)
            .GroupBy(line => line.RuleCode, StringComparer.Ordinal)
            .ToList();

        return
        [
            .. lines
                .Select(group => new RuleInfluenceSummary(
                    group.Key,
                    group.First().Weight,
                    group.Average(line => line.RealisedShare),
                    group.Average(line => line.NominalShare),
                    group.Average(line => line.ValueSpread),
                    group
                        .SelectMany(line => line.Findings)
                        .GroupBy(finding => finding, StringComparer.Ordinal)
                        .ToDictionary(finding => finding.Key, finding => finding.Count(), StringComparer.Ordinal)))
                .OrderByDescending(summary => summary.MeanRealisedShare)
                .ThenBy(summary => summary.RuleCode, StringComparer.Ordinal)
        ];
    }

    private async Task<RuleSet?> LoadAsync(
        Guid id,
        Dictionary<Guid, RuleSet?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        // Archived sets are never deleted precisely so that this lookup succeeds —
        // a trace whose rules are gone says "decided by rules that no longer exist",
        // which is not an explanation.
        var ruleSet = await dbContext.RuleSets
            .AsNoTracking()
            .Include(set => set.Rules)
            .SingleOrDefaultAsync(set => set.Id == id, cancellationToken);

        cache[id] = ruleSet;

        return ruleSet;
    }

    private static ReplayedDecision Unreplayable(Subject subject, string note) =>
        new(subject.Trace.Id,
            subject.Trace.DecidedAt,
            Describe(subject.Inputs),
            ReplayFidelity.Unreplayable,
            note,
            ReplayOutcome.Unchanged,
            subject.Trace.SelectedCandidateKey,
            null,
            null,
            0m,
            0m,
            0,
            0,
            subject.Trace.ConsideredCount,
            0,
            0,
            [],
            [],
            0L);

    private static object? Parse(DecisionKind kind, string json) => kind switch
    {
        DecisionKind.Putaway => TraceJson.Deserialise<PutawayInputs>(json),
        DecisionKind.Allocation => TraceJson.Deserialise<AllocationInputs>(json),
        DecisionKind.CycleCount => TraceJson.Deserialise<CountPolicyInputs>(json),
        DecisionKind.Disposition => TraceJson.Deserialise<DispositionInputs>(json),
        _ => null
    };

    private static Guid WarehouseOf(object inputs) => inputs switch
    {
        PutawayInputs putaway => putaway.WarehouseId,
        AllocationInputs allocation => allocation.WarehouseId,
        CountPolicyInputs counting => counting.WarehouseId,
        DispositionInputs disposition => disposition.WarehouseId,
        _ => Guid.Empty
    };

    /// <summary>What the decision was about, in a form a person scanning a list reads.</summary>
    private static string Describe(object inputs) => inputs switch
    {
        PutawayInputs { PalletContents: { } contents } => $"{contents.Select(line => line.ItemId).Distinct().Count()} SKUs on a pallet",
        PutawayInputs putaway => $"{putaway.Sku} ×{putaway.Quantity:0.###}",
        AllocationInputs allocation => $"{allocation.Sku} ×{allocation.Quantity:0.###}",
        CountPolicyInputs counting => $"up to {counting.MaxTasks} counts",
        DispositionInputs disposition => $"{disposition.Sku} ×{disposition.Quantity:0.###} returned",
        _ => string.Empty
    };

    private sealed record Subject(DecisionTrace Trace, object Inputs);

    private sealed record Comparison(Rerun Baseline, Rerun Candidate, string? Note);
}
