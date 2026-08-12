using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Security;
using Countflow.Simulator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Phase 6 end to end: a warehouse is run forwards until it looks like one, a
/// candidate rule set is replayed against the decisions it actually made, and the
/// comparison says which decisions differ, by how much, and why.
/// <para>
/// <b>The warehouse comes from the simulator, and that is the phase's real claim.</b>
/// Three preference-scoring flaws have been found in this system and every one was
/// found by a person driving a phase through the containers and reading a trace
/// that looked wrong — none by a test. All three were about what a realistic
/// candidate field does to a normaliser, and a hand-built field of two bins is
/// precisely where a broken normaliser and a working one agree. So these tests do
/// not build a field; they build a warehouse and let it build the fields.
/// </para>
/// <para>
/// <b>The control is the identity replay.</b> A rule set replayed against itself
/// must reproduce every decision exactly, and if it does not, the reconstruction is
/// wrong and nothing else in this file means anything. That test is written first
/// deliberately: it is the only one whose failure invalidates the others.
/// </para>
/// </summary>
public sealed class ReplayTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    /// <summary>
    /// The simulated warehouse is built once for the class. Every test here reads
    /// the same history, which is what makes them a set of questions about one
    /// warehouse rather than eight warehouses' worth of setup.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static SimulationReport? _simulation;

    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private HttpClient _admin = null!;
    private string _warehouseCode = string.Empty;

    public async Task InitializeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

            if (!await masterData.Warehouses.AnyAsync())
            {
                var layout = new WarehouseLayoutBuilder().Build();
                masterData.Warehouses.Add(layout.Warehouse);
                masterData.Areas.AddRange(layout.Areas);
                masterData.Zones.AddRange(layout.Zones);
                masterData.Locations.AddRange(layout.Locations);
                await masterData.SaveChangesAsync();
            }

            _warehouseCode = await masterData.Warehouses.Select(w => w.Code).FirstAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("replay-supervisor");
        _operator = await SignInAsync("replay-operator");
        _admin = await SignInAsync("replay-admin");

        await EnsureSimulatedWarehouseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -------------------------------------------------------------- the control

    [Fact]
    public async Task A_rule_set_replayed_against_itself_reproduces_every_putaway_it_made()
    {
        // The load-bearing test in this file. Both sides of a replay run the same
        // rules over one reconstruction of the warehouse, so an identity replay
        // trivially agrees with itself and proves nothing — what it does prove is
        // the other comparison the run makes: whether the rules that made each
        // decision, given the warehouse as rebuilt from the ledger, still choose
        // what the trace says they chose.
        //
        // Putaway is the strictest case available. Its inputs are the item, the
        // quantity and the occupancy of every bin in the building, and occupancy at
        // an instant is exactly what summing the ledger to that instant produces —
        // there is nothing to excuse a miss with. If this is not 100%, the fold in
        // StockHistory is wrong.
        var active = await ActiveRuleSetAsync("Putaway");

        var run = await ReplayAsync("Putaway", active.Id);

        Assert.True(run.ReplayedCount > 10, $"only {run.ReplayedCount} putaway decisions to replay");
        Assert.Equal(run.ReplayedCount, run.ExactCount);
        Assert.Equal(0, run.ChangedCount);
        Assert.Equal(0, run.LostCount);
        Assert.Equal(0, run.ReorderedCount);
    }

    [Fact]
    public async Task A_counting_policy_replayed_against_itself_reproduces_what_it_chose()
    {
        // Counting has one input that genuinely cannot be rebuilt — which holdings
        // were already on somebody's list, which the trace records as a count rather
        // than a set — so this asserts a strong majority rather than everything, and
        // the run reports the shortfall as Approximate rather than hiding it.
        var active = await ActiveRuleSetAsync("CycleCount");

        var run = await ReplayAsync("CycleCount", active.Id);

        Assert.True(run.ReplayedCount > 0, "the simulator raised no count plans");
        Assert.Equal(0, run.UnreplayableCount);
        Assert.True(
            run.ExactCount * 2 >= run.ReplayedCount,
            $"only {run.ExactCount} of {run.ReplayedCount} counting decisions reproduced");
    }

    [Fact]
    public async Task An_allocation_replay_rebuilds_the_commitments_that_were_standing_at_the_time()
    {
        // ADR 0001 said reservations were the one input a replay could not
        // reconstruct, because they are current state rather than an append-only
        // history. They carry CreatedAt and a set-once ClosedAt, their quantity is
        // immutable and their rows are never deleted, so the claims open at an
        // instant come straight out of the table — and this is what says so. It
        // would fail if the reconstruction netted off today's commitments, because
        // by now every one of these orders has been picked and its claims closed.
        var active = await ActiveRuleSetAsync("Allocation");

        var run = await ReplayAsync("Allocation", active.Id);

        Assert.True(run.ReplayedCount > 0, "the simulator released no orders");
        Assert.True(
            run.ExactCount * 2 >= run.ReplayedCount,
            $"only {run.ExactCount} of {run.ReplayedCount} allocation decisions reproduced");
    }

    // ------------------------------------------------------ the acceptance test

    [Fact]
    public async Task The_travel_rule_normalised_against_the_field_is_caught_by_replaying_it()
    {
        // Phase 5 shipped a counting travel rule normalised against the candidate
        // field rather than against the building. Four admissible holdings in two
        // adjacent bays came back scored 0% and 100% along the route, travel
        // contributed a 0.150 swing while the exposure rule it defers to contributed
        // 0.014, and the ranking came out backwards. It was invisible to the test
        // suite and obvious in one container run, which is the whole reason this
        // phase exists.
        //
        // Here it is as a candidate rule set, replayed against a warehouse nobody
        // hand-built, caught by a machine.
        var active = await ActiveRuleSetAsync("CycleCount");

        var candidate = await CloneWithAsync(
            active,
            "count.travel_distance",
            parameters: "{\"scale\":\"Field\"}",
            name: "Cycle count, travel measured against the field");

        var run = await ReplayAsync("CycleCount", candidate.Id);

        // 1. It does something. Against real history and a real field, measuring
        //    travel the other way moves the answer — which is the thing a supervisor
        //    could never find out before this phase without promoting it and waiting.
        Assert.True(
            run.ChangedCount + run.ReorderedCount > 0,
            "the field-normalised policy changed nothing, so the fields it ran against were not realistic");

        // 2. And the report says why, by name. Against the shipped rule set, over
        //    the same decisions and the same warehouse, travel barely separates the
        //    candidates — they are all in the same hall. Measured against the field
        //    instead, the same rule with the same weight stretches those few metres
        //    across a large part of the score, which is exactly what "a few metres
        //    took the full width of the score" looks like as a number.
        var travel = Assert.Single(run.Influence, rule => rule.RuleCode == "count.travel_distance");

        var shipped = run.Decisions
            .SelectMany(decision => decision.BaselineInfluence)
            .Where(line => line.RuleCode == "count.travel_distance")
            .ToList();

        Assert.NotEmpty(shipped);

        var shippedSpread = shipped.Average(line => line.HighestValue - line.LowestValue);

        Assert.True(
            travel.MeanValueSpread > shippedSpread * 3m,
            $"field normalisation spread travel {travel.MeanValueSpread:0.000} and the building spread it " +
            $"{shippedSpread:0.000}; the two scales are not being told apart");
    }

    [Fact]
    public async Task The_same_machine_finds_nothing_to_say_about_the_rule_set_that_is_running()
    {
        // The other half of the guard, and the half that is easy to forget. A
        // detector that fires on everything is not a detector, and the way to find
        // that out is to point it at the rule set the warehouse is actually running
        // — where the counting travel rule measures against the building, as it has
        // since the Phase 5 fix.
        var active = await ActiveRuleSetAsync("CycleCount");

        var run = await ReplayAsync("CycleCount", active.Id);

        var travel = Assert.Single(run.Influence, rule => rule.RuleCode == "count.travel_distance");

        Assert.DoesNotContain("influence.uses_the_full_range", travel.Flags.Keys);
    }

    // ------------------------------------------------------------ what a run is

    [Fact]
    public async Task Changing_a_weight_moves_decisions_and_the_run_says_which_and_by_how_much()
    {
        // The ordinary use: a supervisor wonders what the warehouse would look like
        // if it stopped slotting by velocity band. Before this phase the only way to
        // find out was to promote it and watch the floor for a week.
        var active = await ActiveRuleSetAsync("Putaway");

        var candidate = await CloneWithAsync(
            active,
            "putaway.velocity_slotting",
            weight: 0m,
            name: "Putaway without velocity slotting");

        var run = await ReplayAsync("Putaway", candidate.Id);

        Assert.True(run.ChangedCount > 0, "dropping velocity slotting changed no putaway at all");

        var changed = run.Decisions.First(decision => decision.Outcome == "Changed");

        Assert.NotNull(changed.BaselineKey);
        Assert.NotNull(changed.CandidateKey);
        Assert.NotEqual(changed.BaselineKey, changed.CandidateKey);

        // Where the new winner sat in the old ranking — the number that separates a
        // tuning difference from a change of policy, and the one a diff of two
        // stored traces could never supply, because retention keeps a sample of the
        // losing field rather than all of it.
        Assert.True(changed.CandidateRankUnderBaseline >= 1);

        // And the run is honest about its own credibility beside its conclusions.
        Assert.Equal(run.ReplayedCount, run.ExactCount + run.ApproximateCount + run.UnreplayableCount);
    }

    [Fact]
    public async Task A_replay_is_kept_and_can_be_read_back_with_what_it_said_at_the_time()
    {
        var active = await ActiveRuleSetAsync("Putaway");

        var run = await ReplayAsync("Putaway", active.Id);

        var reread = await Read<ReplayRunBody>(await _supervisor.GetAsync($"/api/replay/runs/{run.Id}"));

        Assert.Equal(run.Reference, reread.Reference);
        Assert.Equal(run.ReplayedCount, reread.ReplayedCount);
        Assert.Equal(run.ExactCount, reread.ExactCount);
        Assert.Equal(run.Influence.Count, reread.Influence.Count);

        var listed = await Read<ReplayRunPageBody>(
            await _supervisor.GetAsync("/api/replay/runs?kind=Putaway&pageSize=50"));

        Assert.Contains(listed.Items, item => item.Id == run.Id);
    }

    [Fact]
    public async Task A_run_can_be_narrowed_to_the_decisions_that_changed()
    {
        var active = await ActiveRuleSetAsync("Putaway");

        var candidate = await CloneWithAsync(
            active, "putaway.velocity_slotting", weight: 0m, name: "Putaway without velocity slotting (filtered)");

        var run = await ReplayAsync("Putaway", candidate.Id);

        var filtered = await Read<ReplayRunBody>(
            await _supervisor.GetAsync($"/api/replay/runs/{run.Id}?outcome=Changed"));

        Assert.Equal(run.ChangedCount, filtered.Decisions.Count);
        Assert.All(filtered.Decisions, decision => Assert.Equal("Changed", decision.Outcome));
    }

    [Fact]
    public async Task A_rule_set_of_the_wrong_kind_is_refused_rather_than_run()
    {
        var allocation = await ActiveRuleSetAsync("Allocation");

        var response = await PostAsync(
            _supervisor,
            "/api/replay/runs",
            new
            {
                warehouseCode = _warehouseCode,
                kind = "Putaway",
                candidateRuleSetId = allocation.Id,
                from = DateTimeOffset.UtcNow.AddDays(-30),
                to = DateTimeOffset.UtcNow.AddDays(1)
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rules.rule_set_kind_mismatch", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_operator_cannot_run_experiments()
    {
        var active = await ActiveRuleSetAsync("Putaway");

        var response = await PostAsync(
            _operator,
            "/api/replay/runs",
            new
            {
                warehouseCode = _warehouseCode,
                kind = "Putaway",
                candidateRuleSetId = active.Id,
                from = DateTimeOffset.UtcNow.AddDays(-30),
                to = DateTimeOffset.UtcNow.AddDays(1)
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------- the editor

    [Fact]
    public async Task A_rule_set_that_could_not_run_is_refused_when_it_is_saved_rather_than_at_the_rack()
    {
        var active = await ActiveRuleSetAsync("Putaway");

        var response = await PostAsync(
            _supervisor,
            "/api/rules/sets",
            new
            {
                kind = "Putaway",
                name = "Putaway with a rule that does not exist",
                cloneFromRuleSetId = active.Id,
                rules = new[] { new { code = "putaway.prefer_bays_under_the_mezzanine", order = 10, weight = 0.5m } }
            });

        // Not at the next dock receipt, with a pallet on a forklift and a receipt
        // half done — which is where an unvalidated set fails.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rules.rule_not_in_vocabulary", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rule_from_another_vocabulary_cannot_be_put_in_a_set()
    {
        var active = await ActiveRuleSetAsync("CycleCount");

        var response = await PostAsync(
            _supervisor,
            "/api/rules/sets",
            new
            {
                kind = "CycleCount",
                name = "Counting with an allocation rule in it",
                cloneFromRuleSetId = active.Id,
                rules = new[] { new { code = "allocation.fefo", order = 10, weight = 0.5m } }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_set_the_warehouse_is_running_cannot_be_edited()
    {
        // It is evidence rather than a document: every trace it produced points at
        // it, and editing one would rewrite the explanation behind pallets that have
        // already been moved.
        var active = await ActiveRuleSetAsync("Putaway");

        var response = await _supervisor.PutAsJsonAsync(
            $"/api/rules/sets/{active.Id}",
            new
            {
                rules = active.Rules.Select(rule => new { code = rule.Code, order = rule.Order, weight = rule.Weight })
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("rules.rule_set_not_editable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Promoting_a_draft_archives_what_it_replaces_and_takes_the_next_version()
    {
        var active = await ActiveRuleSetAsync("Allocation");

        var draft = await CloneWithAsync(
            active, "allocation.travel_distance", weight: 0.12m, name: "Allocation, promoted by a test");

        Assert.Equal("Draft", draft.Status);
        Assert.Equal(0, draft.Version);

        var promoted = await Read<RuleSetBody>(await PostAsync(
            _admin, $"/api/rules/sets/{draft.Id}/promote", new { }));

        Assert.Equal("Active", promoted.Status);
        Assert.Equal(active.Version + 1, promoted.Version);

        var superseded = await FindRuleSetAsync(active.Id);

        Assert.Equal("Archived", superseded.Status);

        // Put the warehouse back the way the other tests expect to find it. The
        // archived set is promoted again rather than resurrected in place, which is
        // the only route the editor offers and is the point.
        var restore = await Read<RuleSetBody>(await PostAsync(
            _supervisor,
            "/api/rules/sets",
            new
            {
                kind = "Allocation",
                name = "Standard allocation (restored)",
                cloneFromRuleSetId = active.Id
            }));

        await Read<RuleSetBody>(await PostAsync(_admin, $"/api/rules/sets/{restore.Id}/promote", new { }));
    }

    [Fact]
    public async Task A_supervisor_may_draft_but_only_an_admin_may_put_it_on_the_floor()
    {
        // Asking what a change would do should be cheap and frequent; making it real
        // should not be.
        var active = await ActiveRuleSetAsync("Putaway");

        var draft = await CloneWithAsync(
            active, "putaway.cube_utilisation", weight: 0.2m, name: "Putaway draft a supervisor may not promote");

        var response = await PostAsync(_supervisor, $"/api/rules/sets/{draft.Id}/promote", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------- the warehouse

    /// <summary>
    /// Runs the simulator once for the class, and checks that what came out is
    /// actually a warehouse.
    /// <para>
    /// The assertions here are not ceremony. Every test below reads this history,
    /// and a simulator that quietly received nothing would leave them all passing
    /// against an empty database — which is exactly the failure mode a machine built
    /// to replace a manual container run must not have.
    /// </para>
    /// </summary>
    private async Task EnsureSimulatedWarehouseAsync()
    {
        await Gate.WaitAsync();

        try
        {
            if (_simulation is not null)
            {
                return;
            }

            // The fixture runs the seeders but leaves demo data off, so the
            // catalogue is empty and there is nothing to receive. A warehouse needs
            // a catalogue before it needs anything else.
            await SeedCatalogueAsync(24);

            var simulator = new WarehouseSimulator(_admin, _operator);

            var report = await simulator.RunAsync(new SimulationPlan(
                WarehouseCode: _warehouseCode,
                Days: 3,
                ReceiptsPerDay: 4,
                LinesPerReceipt: 3,
                OrdersPerDay: 2,
                LinesPerOrder: 2,
                CountsPerDay: 8,
                SkuPoolSize: 24,
                Seed: 20260812));

            var refused = report.Notes.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, report.Notes.Take(10));

            Assert.True(report.Receipts >= 12, $"the simulator received almost nothing: {report}{refused}");
            Assert.True(report.Putaways >= 10, $"the simulator put almost nothing away: {report}{refused}");
            Assert.True(report.Orders >= 4, $"the simulator shipped almost nothing: {report}{refused}");

            // The counts matter most of all: they are append-only, timestamped, and
            // the only record of whether the numbers the system publishes are true.
            Assert.True(report.CountPlans >= 1, $"the simulator counted nothing: {report}{refused}");

            _simulation = report;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// A small catalogue with the variety the rules are written about.
    /// <para>
    /// Three velocity bands, because the counting policy paces itself by them and a
    /// warehouse of one band would make that rule score every candidate identically.
    /// Lot and expiry tracking with a spread of shelf lives, because FEFO ranks
    /// batches by the fraction of their own life left and a catalogue with one shelf
    /// life would hide the difference between sixty days on a two-year product and
    /// sixty days on a three-week one. Ambient only: chilled stock received onto an
    /// ambient dock is refused by every storage location, which is a good test of the
    /// trace and a poor way to fill a warehouse.
    /// </para>
    /// </summary>
    private async Task SeedCatalogueAsync(int count)
    {
        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        if (await masterData.Items.AnyAsync(item => item.Sku.StartsWith("SIM-")))
        {
            return;
        }

        for (var index = 1; index <= count; index++)
        {
            var item = new Item
            {
                Sku = $"SIM-{index:D4}",
                Description = $"Simulated line {index}",
                BaseUom = "EA",
                AbcClass = (index % 3) switch { 0 => AbcClass.A, 1 => AbcClass.B, _ => AbcClass.C },
                TemperatureClass = TemperatureClass.Ambient,
                IsLotTracked = true,
                IsExpiryTracked = true,
                ShelfLifeDays = 90 + (index % 5 * 180),
                MinimumRemainingShelfLifePercent = 20
            };

            item.PackConfigurations.Add(new PackConfiguration
            {
                ItemId = item.Id,
                Level = PackagingLevel.Each,
                BaseUnitQuantity = 1,
                LengthMm = 120m,
                WidthMm = 100m,
                HeightMm = 90m,
                GrossWeightKg = 0.4m + (index % 7 * 0.1m),
                IsPickUnit = true
            });

            masterData.Items.Add(item);
        }

        await masterData.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<RuleSetBody> ActiveRuleSetAsync(string kind)
    {
        var sets = await Read<RuleSetsBody>(await _supervisor.GetAsync($"/api/rules/sets?kind={kind}"));

        return sets.Items.First(set => set.Status == "Active");
    }

    private async Task<RuleSetBody> FindRuleSetAsync(Guid id)
    {
        var sets = await Read<RuleSetsBody>(await _supervisor.GetAsync("/api/rules/sets"));

        return sets.Items.First(set => set.Id == id);
    }

    /// <summary>
    /// A copy of a rule set with one rule changed. The path a supervisor takes, and
    /// the only path there is: an active set cannot be edited, so a change is
    /// always a new draft.
    /// </summary>
    private async Task<RuleSetBody> CloneWithAsync(
        RuleSetBody original,
        string code,
        string name,
        decimal? weight = null,
        string? parameters = null)
    {
        var rules = original.Rules
            .Select(rule => new
            {
                code = rule.Code,
                order = rule.Order,
                weight = rule.Code == code && weight is { } changed ? changed : rule.Weight,
                parameters = rule.Code == code && parameters is not null ? parameters : rule.Parameters,
                isEnabled = rule.IsEnabled
            })
            .ToList();

        Assert.Contains(original.Rules, rule => rule.Code == code);

        return await Read<RuleSetBody>(await PostAsync(
            _supervisor,
            "/api/rules/sets",
            new
            {
                kind = original.Kind,
                name,
                cloneFromRuleSetId = original.Id,
                rules
            }));
    }

    private async Task<ReplayRunBody> ReplayAsync(string kind, Guid candidateRuleSetId) =>
        await Read<ReplayRunBody>(await PostAsync(
            _supervisor,
            "/api/replay/runs",
            new
            {
                warehouseCode = _warehouseCode,
                kind,
                candidateRuleSetId,
                from = DateTimeOffset.UtcNow.AddDays(-2),
                to = DateTimeOffset.UtcNow.AddDays(1),
                maxDecisions = 400
            }));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body) =>
        client.PostAsJsonAsync(route, body);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task EnsureUsersAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<CountflowRole>>();
        var users = services.GetRequiredService<UserManager<CountflowUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new CountflowRole(role));
            }
        }

        await EnsureAsync("replay-supervisor", "RPL-SUP", Roles.Supervisor);
        await EnsureAsync("replay-operator", "RPL-OP1", Roles.Operator);
        await EnsureAsync("replay-admin", "RPL-ADM", Roles.Admin);

        async Task EnsureAsync(string userName, string operatorCode, string role)
        {
            if (await users.FindByNameAsync(userName) is not null)
            {
                return;
            }

            var user = new CountflowUser
            {
                UserName = userName,
                Email = $"{userName}@countflow.test",
                EmailConfirmed = true,
                DisplayName = userName,
                OperatorCode = operatorCode
            };

            var created = await users.CreateAsync(user, Secret);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            await users.AddToRoleAsync(user, role);
        }
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(userName, Secret));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);

        return client;
    }

    // ------------------------------------------------------------- wire shapes

    private sealed record LoginResponse(string AccessToken);

    private sealed record RuleBody(string Code, int Order, decimal Weight, string? Parameters, bool IsEnabled);

    private sealed record RuleSetBody(
        Guid Id,
        string Kind,
        string Name,
        int Version,
        string Status,
        IReadOnlyList<RuleBody> Rules);

    private sealed record RuleSetsBody(IReadOnlyList<RuleSetBody> Items);

    private sealed record InfluenceLineBody(
        string RuleCode,
        decimal Weight,
        decimal LowestValue,
        decimal HighestValue,
        decimal RealisedSwing,
        decimal RealisedShare,
        decimal NominalShare,
        decimal MarginContribution);

    private sealed record InfluenceSummaryBody(
        string RuleCode,
        decimal Weight,
        decimal MeanRealisedShare,
        decimal MeanNominalShare,
        decimal MeanValueSpread,
        IReadOnlyDictionary<string, int> Flags);

    private sealed record ReplayDecisionBody(
        Guid DecisionTraceId,
        string Subject,
        string Fidelity,
        string? FidelityNote,
        string Outcome,
        string? RecordedKey,
        string? BaselineKey,
        string? CandidateKey,
        decimal BaselineScore,
        decimal CandidateScore,
        int CandidateRankUnderBaseline,
        int BaselineRankUnderCandidate,
        IReadOnlyList<InfluenceLineBody> BaselineInfluence,
        IReadOnlyList<InfluenceLineBody> CandidateInfluence);

    private sealed record ReplayRunBody(
        Guid Id,
        string Reference,
        int ReplayedCount,
        int ExactCount,
        int ApproximateCount,
        int UnreplayableCount,
        int ChangedCount,
        int ReorderedCount,
        int GainedCount,
        int LostCount,
        IReadOnlyList<InfluenceSummaryBody> Influence,
        IReadOnlyList<ReplayDecisionBody> Decisions);

    private sealed record ReplayRunPageBody(IReadOnlyList<ReplayRunBody> Items);
}
