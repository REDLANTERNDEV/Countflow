using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;

namespace Countflow.IntegrationTests;

/// <summary>
/// Phase 3 end to end: a pallet is received on the dock, the rules choose where it
/// goes and say why, an operator confirms it, and the ledger ends up holding both
/// movements with the reasoning attached.
/// <para>
/// The class exists to prove the sentence above, which is the phase's definition
/// of done. Everything else here defends the properties that make it worth
/// anything: that the suggestion is deterministic and explainable, that an
/// override is recorded rather than silently accepted, and that every step
/// survives the retry a handheld on a flaky link will certainly send.
/// </para>
/// </summary>
public sealed partial class InboundLoopTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private HttpClient _secondOperator = null!;
    private string _dock = string.Empty;
    private string _warehouseCode = string.Empty;
    private int _counter;

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

            _dock = await masterData.Locations
                .Where(l => l.Type == LocationType.ReceivingDock)
                .OrderBy(l => l.Code)
                .Select(l => l.Code)
                .FirstAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("inbound-supervisor");
        _operator = await SignInAsync("inbound-operator");
        _secondOperator = await SignInAsync("inbound-operator-2");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------- the loop

    [Fact]
    public async Task A_received_pallet_is_suggested_a_location_confirmed_and_left_traced_in_the_ledger()
    {
        var sku = await NewItemAsync();
        var sscc = NewSscc();

        // 1. Goods come off the truck onto the dock.
        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 48m, toLocationCode = _dock, sscc }));

        Assert.Equal(_dock, received.ToLocationCode);
        Assert.NotEqual(Guid.Empty, received.DecisionTraceId);

        // 2. The rules chose somewhere, out of a field of real candidates.
        Assert.NotNull(received.SuggestedLocationCode);
        Assert.True(received.ConsideredCount > 100, $"only {received.ConsideredCount} candidates considered");
        Assert.True(received.AdmissibleCount > 0);
        Assert.NotNull(received.WorkTaskId);

        // 3. The operator takes the job and sees why that bin.
        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{received.WorkTaskId}/claim", new { }));

        Assert.Equal(received.WorkTaskId, claimed.Task.Id);
        Assert.Equal("Putaway", claimed.Task.Kind);
        Assert.Equal(received.SuggestedLocationCode, claimed.Task.SuggestedLocationCode);
        Assert.Equal(sscc, claimed.Task.Sscc);

        Assert.NotNull(claimed.Decision);
        Assert.Equal(received.SuggestedLocationCode, claimed.Decision.SelectedKey);
        Assert.Equal("Standard putaway", claimed.Decision.RuleSetName);

        // The winner's score breakdown is what "why here?" actually shows, and it
        // is codes rather than prose so the handheld can render it in Turkish.
        var winner = claimed.Decision.Candidates.Single(c => c.CandidateKey == received.SuggestedLocationCode);
        Assert.True(winner.IsAdmissible);
        Assert.Equal(1, winner.Rank);
        Assert.NotEmpty(winner.Scores);
        Assert.All(winner.Scores, s => Assert.StartsWith("rules.putaway.", s.Reason.Code));

        // And a bin that lost says which rule ruled it out.
        var excluded = claimed.Decision.Candidates.FirstOrDefault(c => !c.IsAdmissible);
        Assert.NotNull(excluded);
        Assert.NotNull(excluded.ExcludedBy);
        Assert.StartsWith("rules.putaway.", excluded.ExcludedBy.Code);

        // 4. They confirm it at the bin they were sent to.
        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{claimed.Task.Id}/complete",
            new { toLocationCode = received.SuggestedLocationCode }));

        Assert.Equal("Completed", completed.Task.Status);
        Assert.False(completed.Task.WasOverridden);
        Assert.Single(completed.Movements);

        // 5. The ledger holds both movements, and the putaway carries the trace.
        var movements = await MovementsAsync(sku);
        Assert.Equal(2, movements.Count);

        var receipt = movements.Single(m => m.Type == "Receipt");
        Assert.Equal(_dock, receipt.ToLocationCode);
        Assert.Null(receipt.DecisionTraceId);

        var putaway = movements.Single(m => m.Type == "Putaway");
        Assert.Equal(_dock, putaway.FromLocationCode);
        Assert.Equal(received.SuggestedLocationCode, putaway.ToLocationCode);
        Assert.Equal(48m, putaway.Quantity);

        // The whole point of the phase, in two assertions: the movement knows
        // which job produced it and which evaluation justified it.
        Assert.Equal(claimed.Task.Id, putaway.TaskId);
        Assert.Equal(received.DecisionTraceId, putaway.DecisionTraceId);

        // And the stock really moved.
        Assert.Equal(0m, await BalanceAsync(sku, _dock));
        Assert.Equal(48m, await BalanceAsync(sku, received.SuggestedLocationCode!));
    }

    // -------------------------------------------------------------- the rules

    [Fact]
    public async Task The_same_pallet_scanned_twice_is_sent_to_the_same_place()
    {
        var sku = await NewItemAsync();

        var first = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 10m, toLocationCode = _dock }));

        var second = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 10m, toLocationCode = _dock }));

        // Not merely equal by luck: the second receipt sees the first still on the
        // dock, and consolidation would only pull it elsewhere if the first had
        // already been put away. An operator rescanning a pallet and being sent
        // somewhere else is how trust in directed work dies.
        Assert.NotNull(first.SuggestedLocationCode);
        Assert.Equal(first.SuggestedLocationCode, second.SuggestedLocationCode);
    }

    [Fact]
    public async Task A_chilled_item_is_never_sent_to_an_ambient_aisle_and_the_trace_says_so()
    {
        var sku = await NewItemAsync(temperature: TemperatureClass.Chilled);

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 12m, toLocationCode = _dock }));

        Assert.NotNull(received.SuggestedLocationCode);
        Assert.Contains("-CHL-", received.SuggestedLocationCode);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{received.DecisionTraceId}");

        // Every admissible candidate is chilled, and at least one ambient bin was
        // considered and rejected by name. "It never even looked" is the answer a
        // supervisor cannot get from a WHERE clause.
        Assert.All(
            trace.Candidates.Where(c => c.IsAdmissible),
            c => Assert.Contains("-CHL-", c.CandidateKey));

        var mismatch = trace.Candidates
            .Where(c => c.ExcludedBy?.Code == "rules.putaway.temperature_mismatch")
            .ToList();

        Assert.NotEmpty(mismatch);

        // Named arguments, not positional: Turkish reorders the clause, so a
        // translator has to be able to move one in front of the other.
        var reason = mismatch[0].ExcludedBy!;
        Assert.Equal("Chilled", reason.Args["item"]);
        Assert.Equal("Ambient", reason.Args["location"]);
    }

    [Fact]
    public async Task Goods_with_nowhere_to_go_still_produce_a_task_and_an_explanation()
    {
        // The seeded warehouse has ambient and chilled zones and no freezer, so a
        // frozen item has no admissible bin anywhere in the building.
        var sku = await NewItemAsync(temperature: TemperatureClass.Frozen);

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 6m, toLocationCode = _dock }));

        Assert.Null(received.SuggestedLocationCode);
        Assert.Equal(0, received.AdmissibleCount);

        // The pallet is on the dock either way, so the work is real. What the
        // system owes the operator is the reason, not silence.
        Assert.NotNull(received.WorkTaskId);
        Assert.NotEqual(Guid.Empty, received.DecisionTraceId);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{received.DecisionTraceId}");

        Assert.True(trace.ConsideredCount > 100);
        Assert.Equal(0, trace.AdmissibleCount);
        Assert.All(trace.Candidates, c => Assert.NotNull(c.ExcludedBy));
    }

    [Fact]
    public async Task A_preview_answers_the_same_question_without_moving_anything()
    {
        var sku = await NewItemAsync();

        var preview = await Read<PreviewBody>(await PostAsync(
            _supervisor, "/api/v1/rules/putaway/preview",
            new { sku, quantity = 24m, fromLocationCode = _dock }));

        Assert.NotNull(preview.LocationCode);
        Assert.NotEmpty(preview.Trace.Candidates);

        // Nothing was posted, so the item has no ledger history at all.
        Assert.Empty(await MovementsAsync(sku));
    }

    [Fact]
    public async Task An_operator_cannot_see_the_rule_set_but_can_always_see_the_reasoning()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 5m, toLocationCode = _dock }));

        // Rule sets are configuration. Traces are the explanation an operator is
        // owed at the bin — hiding them behind a supervisor role would ship the
        // feature and lose the benefit (ADR 0004).
        Assert.Equal(HttpStatusCode.Forbidden, (await _operator.GetAsync("/api/v1/rules/sets")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _operator.GetAsync($"/api/v1/rules/traces/{received.DecisionTraceId}")).StatusCode);
    }

    // ----------------------------------------------------------- the operator

    [Fact]
    public async Task Confirming_somewhere_else_is_recorded_as_an_override_rather_than_refused()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 20m, toLocationCode = _dock }));

        var elsewhere = await ADifferentAmbientBinAsync(received.SuggestedLocationCode!);

        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = elsewhere, overrideReason = "Racking damaged" }));

        // The floor is allowed to disagree with the rules. What matters is that
        // the disagreement is measurable — it is the number that tells a
        // supervisor their rule set needs work.
        Assert.True(completed.Task.WasOverridden);
        Assert.Equal("Racking damaged", completed.Task.OverrideReason);
        Assert.Equal(elsewhere, completed.Task.CompletedAtLocationCode);

        // The movement still carries the trace: the reasoning that was offered is
        // part of the record even when it was not taken.
        var putaway = (await MovementsAsync(sku)).Single(m => m.Type == "Putaway");
        Assert.Equal(elsewhere, putaway.ToLocationCode);
        Assert.Equal(received.DecisionTraceId, putaway.DecisionTraceId);
    }

    [Fact]
    public async Task A_retried_confirmation_posts_once()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 30m, toLocationCode = _dock }));

        var url = $"/api/v1/work/tasks/{received.WorkTaskId}/complete";
        var body = new { toLocationCode = received.SuggestedLocationCode };
        var key = $"confirm-{sku}";

        var first = await PostAsync(_operator, url, body, key);
        var second = await PostAsync(_operator, url, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(2, (await MovementsAsync(sku)).Count);
        Assert.Equal(30m, await BalanceAsync(sku, received.SuggestedLocationCode!));
    }

    [Fact]
    public async Task A_second_confirmation_under_a_new_key_is_refused()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 30m, toLocationCode = _dock }));

        var url = $"/api/v1/work/tasks/{received.WorkTaskId}/complete";
        var body = new { toLocationCode = received.SuggestedLocationCode };

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, url, body, $"a-{sku}")).StatusCode);

        // Not a retry — a second confirmation of work already done, which against
        // an append-only ledger would move the stock twice.
        var again = await PostAsync(_operator, url, body, $"b-{sku}");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(2, (await MovementsAsync(sku)).Count);
    }

    [Fact]
    public async Task Two_operators_claiming_at_once_are_never_given_the_same_task()
    {
        var skus = new List<string>();

        for (var index = 0; index < 6; index++)
        {
            var sku = await NewItemAsync();
            skus.Add(sku);

            await PostAsync(_operator, "/api/v1/inbound/receipts",
                new { sku, quantity = 4m, toLocationCode = _dock });
        }

        // Both press "next job" at the same moment, which is what a shift start
        // actually looks like.
        var claims = await Task.WhenAll(
            Enumerable.Range(0, 3).SelectMany(_ => new[]
            {
                PostAsync(_operator, "/api/v1/work/tasks/claim-next", new { }),
                PostAsync(_secondOperator, "/api/v1/work/tasks/claim-next", new { })
            }));

        var claimed = new List<Guid>();

        foreach (var response in claims.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            claimed.Add((await Read<TaskDetail>(response)).Task.Id);
        }

        Assert.Equal(6, claimed.Count);
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
    }

    [Fact]
    public async Task A_released_task_goes_back_to_the_queue()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 8m, toLocationCode = _dock }));

        var release = await PostAsync(_operator, $"/api/v1/work/tasks/{received.WorkTaskId}/release", new { });
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);

        var claimed = await Read<TaskDetail>(await PostAsync(_operator, "/api/v1/work/tasks/claim-next", new { }));

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostAsync(_operator, $"/api/v1/work/tasks/{claimed.Task.Id}/release", new { })).StatusCode);

        var detail = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{claimed.Task.Id}");
        Assert.Equal("Ready", detail.Task.Status);
    }

    [Fact]
    public async Task One_operators_task_cannot_be_completed_by_another()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 8m, toLocationCode = _dock }));

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync(_operator, $"/api/v1/work/tasks/{received.WorkTaskId}/claim", new { })).StatusCode);

        var stolen = await PostAsync(
            _secondOperator,
            $"/api/v1/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = received.SuggestedLocationCode });

        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

        // And a second operator cannot take it out from under the first either.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await PostAsync(_secondOperator, $"/api/v1/work/tasks/{received.WorkTaskId}/claim", new { })).StatusCode);
    }

    // ---------------------------------------------------------- whole pallets

    [Theory]
    [InlineData(TemperatureClass.Ambient, TemperatureClass.Frozen)]
    [InlineData(TemperatureClass.Frozen, TemperatureClass.Ambient)]
    public async Task A_merged_receipt_returns_the_tasks_effective_explanation(TemperatureClass firstTemperature, TemperatureClass secondTemperature)
    {
        var first = await NewItemAsync(firstTemperature, caller: $"Merged-{firstTemperature}");
        var second = await NewItemAsync(secondTemperature, caller: $"Merged-{firstTemperature}");
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        var two = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));
        Assert.Equal(one.WorkTaskId, two.WorkTaskId);
        var task = await GetAsync<JsonElement>($"/api/v1/work/tasks/{two.WorkTaskId}");
        Assert.Equal(task.GetProperty("task").GetProperty("decisionTraceId").GetGuid(), two.DecisionTraceId);
        Assert.Equal(one.DecisionTraceId, two.DecisionTraceId);
        Assert.Equal(task.GetProperty("task").GetProperty("suggestedLocationCode").GetString(), two.SuggestedLocationCode);
        Assert.Equal(task.GetProperty("decision").GetProperty("consideredCount").GetInt32(), two.ConsideredCount);
        Assert.Equal(task.GetProperty("decision").GetProperty("admissibleCount").GetInt32(), two.AdmissibleCount);
    }

    [Fact]
    public async Task Receipt_replay_returns_current_revision_and_new_receipts_suppress_stale_guidance()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var firstBody = new { sku = first, quantity = 10m, toLocationCode = _dock, sscc };
        var key = Guid.NewGuid().ToString();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", firstBody, key));
        var revised = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{one.WorkTaskId}/reslot", new { reason = "Unloading finished" }));
        var current = revised.GetProperty("current").GetProperty("task");
        var retry = await Read<JsonElement>(await PostAsync(_operator, "/api/v1/inbound/receipts", firstBody, key));
        Assert.Equal(current.GetProperty("decisionTraceId").GetGuid(), retry.GetProperty("decisionTraceId").GetGuid());
        Assert.Equal(1, retry.GetProperty("putawayRevision").GetInt64());
        Assert.False(retry.GetProperty("putawayAdviceStale").GetBoolean());
        Assert.Equal(current.GetProperty("suggestedLocationCode").GetString(), retry.GetProperty("suggestedLocationCode").GetString());
        var newReceipt = await Read<JsonElement>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));
        Assert.Equal(current.GetProperty("decisionTraceId").GetGuid(), newReceipt.GetProperty("decisionTraceId").GetGuid());
        Assert.Equal(1, newReceipt.GetProperty("putawayRevision").GetInt64());
        Assert.True(newReceipt.GetProperty("putawayAdviceStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, newReceipt.GetProperty("suggestedLocationCode").ValueKind);
    }

    [Fact]
    public async Task Re_slotting_a_mixed_pallet_preserves_the_old_trace_and_fences_old_confirmations()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));
        var before = await GetAsync<JsonElement>($"/api/v1/rules/traces/{one.DecisionTraceId}");
        var url = $"/api/v1/work/tasks/{one.WorkTaskId}/reslot";
        var key = Guid.NewGuid().ToString();
        var body = new { reason = "Unloading finished; choose for the whole pallet" };
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_operator, url, body, key)).StatusCode);
        var applied = await Read<JsonElement>(await PostAsync(_supervisor, url, body, key));
        var current = applied.GetProperty("current");
        var task = current.GetProperty("task");
        var trace = task.GetProperty("decisionTraceId").GetGuid();
        Assert.NotEqual(one.DecisionTraceId, trace);
        Assert.Equal(1, task.GetProperty("putawayRevision").GetInt64());
        Assert.False(task.GetProperty("putawayAdviceStale").GetBoolean());
        var revision = applied.GetProperty("appliedRevision");
        Assert.Equal(one.DecisionTraceId, revision.GetProperty("previousDecisionTraceId").GetGuid());
        Assert.Equal(2, revision.GetProperty("contents").GetArrayLength());
        Assert.Single(current.GetProperty("putawayHistory").EnumerateArray());
        var after = await GetAsync<JsonElement>($"/api/v1/rules/traces/{one.DecisionTraceId}");
        Assert.Equal(before.GetRawText(), after.GetRawText());
        var stale = await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
            new { toLocationCode = one.SuggestedLocationCode, expectedDecisionTraceId = one.DecisionTraceId });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(30m, await BalanceAsync(first, _dock) + await BalanceAsync(second, _dock));
        var completed = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
            new { toLocationCode = task.GetProperty("suggestedLocationCode").GetString(), expectedDecisionTraceId = trace }));
        Assert.Equal(2, completed.Movements.Count);
        Assert.Equal(trace, (await MovementsAsync(first)).Single(m => m.Type == "Putaway").DecisionTraceId);
        var replay = await Read<JsonElement>(await PostAsync(_supervisor, url, body, key));
        Assert.True(replay.GetProperty("wasAlreadyApplied").GetBoolean());
        Assert.Equal(trace, replay.GetProperty("appliedRevision").GetProperty("decisionTraceId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_supervisor, url, new { reason = "Changed body" }, key)).StatusCode);
    }

    [Fact]
    public async Task Revised_completion_refuses_an_inventory_change_without_a_receipt_handoff()
    {
        var sku = await NewItemAsync();
        var sscc = NewSscc();
        var receipt = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = _dock, sscc }));
        var revised = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Unload finished" }));
        var task = revised.GetProperty("current").GetProperty("task");
        // An Inventory writer outside Inbound does not update Work's receipt counter.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var item = await db.Items.SingleAsync(row => row.Sku == sku);
            var dock = await db.Locations.SingleAsync(row => row.Code == _dock);
            var added = await scope.ServiceProvider.GetRequiredService<Countflow.SharedKernel.Contracts.IStockPosting>()
                .ReceiveAsync(new Countflow.SharedKernel.Contracts.ReceiptInstruction
                {
                    ItemId = item.Id, Quantity = 1m, ToLocationId = dock.Id, Sscc = sscc,
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    Actor = new Countflow.SharedKernel.Contracts.PostingActor(Guid.NewGuid(), "external-receipt")
                }, CancellationToken.None);
            Assert.True(added.IsSuccess);
        }
        var refused = await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = task.GetProperty("suggestedLocationCode").GetString(),
                expectedDecisionTraceId = task.GetProperty("decisionTraceId").GetGuid() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("inventory.pallet_contents_changed", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(11m, await BalanceAsync(sku, _dock));
        Assert.DoesNotContain(await MovementsAsync(sku), movement => movement.Type == "Putaway");
        var refreshed = await Read<JsonElement>(await PostAsync(_supervisor,
            $"/api/v1/work/tasks/{receipt.WorkTaskId}/reslot", new { reason = "Account for external stock change" }));
        var refreshedTask = refreshed.GetProperty("current").GetProperty("task");
        Assert.Equal(11m, refreshedTask.GetProperty("quantity").GetDecimal());
        var done = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/complete",
            new { toLocationCode = refreshedTask.GetProperty("suggestedLocationCode").GetString(),
                expectedDecisionTraceId = refreshedTask.GetProperty("decisionTraceId").GetGuid() }));
        Assert.Equal(11m, Assert.Single(done.Movements).Quantity);
    }

    [Fact]
    public async Task New_receipts_make_a_re_slotted_pallet_stale_until_another_revision_is_recorded()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        var url = $"/api/v1/work/tasks/{one.WorkTaskId}/reslot";
        var old = await Read<JsonElement>(await PostAsync(_supervisor, url, new { reason = "First load" }));
        var oldTrace = old.GetProperty("appliedRevision").GetProperty("decisionTraceId").GetGuid();
        await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));
        var stale = await GetAsync<JsonElement>($"/api/v1/work/tasks/{one.WorkTaskId}");
        Assert.True(stale.GetProperty("task").GetProperty("putawayAdviceStale").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
            new { toLocationCode = one.SuggestedLocationCode, expectedDecisionTraceId = oldTrace })).StatusCode);
        var next = await Read<JsonElement>(await PostAsync(_supervisor, url, new { reason = "Second SKU arrived" }));
        var task = next.GetProperty("current").GetProperty("task");
        Assert.Equal(2, task.GetProperty("putawayRevision").GetInt64());
        Assert.Equal(2, next.GetProperty("current").GetProperty("putawayHistory").GetArrayLength());
        Assert.False(task.GetProperty("putawayAdviceStale").GetBoolean());
        var completed = await Read<TaskCompletion>(await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
            new { toLocationCode = task.GetProperty("suggestedLocationCode").GetString(), expectedDecisionTraceId = task.GetProperty("decisionTraceId").GetGuid() }));
        Assert.Equal(2, completed.Movements.Count);
    }

    [Fact]
    public async Task Concurrent_re_slot_retries_apply_one_revision_and_assigned_tasks_refuse_new_decisions()
    {
        var sku = await NewItemAsync();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = _dock, sscc = NewSscc() }));
        var url = $"/api/v1/work/tasks/{one.WorkTaskId}/reslot";
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostAsync(_supervisor, url, new { reason = "Whole load" }, key)));
        var applied = await Task.WhenAll(responses.Select(Read<JsonElement>));
        Assert.Single(applied, value => !value.GetProperty("wasAlreadyApplied").GetBoolean());
        Assert.All(applied, value => Assert.Single(value.GetProperty("current").GetProperty("putawayHistory").EnumerateArray()));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_supervisor, url, new { reason = "Changed while held" })).StatusCode);
    }

    [Fact]
    public async Task An_unacknowledged_pallet_receipt_blocks_claim_and_completion_until_its_source_lands()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        var gate = new ReceiptAdviceGate();
        using var pausedApp = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IPutawayAdvisor>(provider =>
                new PausedReceiptAdvisor(provider.GetRequiredService<PutawayAdvisor>(), gate)))));
        using var receiving = pausedApp.CreateClient();
        receiving.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var pending = PostAsync(receiving, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Inventory has committed the second receipt; Work has not yet seen
            // its source acknowledgement. Moving now used to create a ghost job
            // when that acknowledgement finally arrived.
            var claim = await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { });
            Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
            var reslot = await PostAsync(_supervisor, $"/api/v1/work/tasks/{one.WorkTaskId}/reslot", new { reason = "Still receiving" });
            Assert.Equal(HttpStatusCode.Conflict, reslot.StatusCode);
            var complete = await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
                new { toLocationCode = one.SuggestedLocationCode });
            Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
            Assert.Equal(20m, await BalanceAsync(second, _dock));
        }
        finally
        {
            gate.Resume.TrySetResult();
            await pending;
        }
        var two = await Read<DockReceipt>(await pending);
        Assert.Equal(one.WorkTaskId, two.WorkTaskId);
        var completed = await Read<TaskCompletion>(await PostAsync(_operator,
            $"/api/v1/work/tasks/{one.WorkTaskId}/complete", new { toLocationCode = one.SuggestedLocationCode }));
        Assert.Equal(2, completed.Movements.Count);
        Assert.Equal(0m, await BalanceAsync(first, _dock));
        Assert.Equal(0m, await BalanceAsync(second, _dock));
    }

    private sealed class ReceiptAdviceGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RefuseAdvice { get; init; }
    }

    [Fact]
    public async Task Simultaneous_first_receipts_keep_the_new_task_blocked_until_both_are_acknowledged()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var gate = new ReceiptAdviceGate();
        using var pausedApp = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IPutawayAdvisor>(provider =>
                new PausedReceiptAdvisor(provider.GetRequiredService<PutawayAdvisor>(), gate)))));
        using var receiving = pausedApp.CreateClient();
        receiving.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var pending = PostAsync(receiving, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc });
        DockReceipt two;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            two = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
                new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));
            var claim = await PostAsync(_operator, $"/api/v1/work/tasks/{two.WorkTaskId}/claim", new { });
            Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        }
        finally { gate.Resume.TrySetResult(); await pending; }
        var one = await Read<DockReceipt>(await pending);
        Assert.Equal(two.WorkTaskId, one.WorkTaskId);
        var completed = await Read<TaskCompletion>(await PostAsync(_operator,
            $"/api/v1/work/tasks/{one.WorkTaskId}/complete", new { toLocationCode = two.SuggestedLocationCode }));
        Assert.Equal(2, completed.Movements.Count);
    }

    [Fact]
    public async Task A_definite_receipt_refusal_clears_the_pending_handoff()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        var refused = await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc, expiryDate = "2027-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0m, await BalanceAsync(second, _dock));
        var claim = await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var more = await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc });
        Assert.Equal(HttpStatusCode.Conflict, more.StatusCode);
        Assert.Equal(0m, await BalanceAsync(second, _dock));
    }

    private sealed class PausedReceiptAdvisor(PutawayAdvisor inner, ReceiptAdviceGate gate) : IPutawayAdvisor
    {
        public async Task<Result<PutawayAdvice>> SuggestAsync(PutawayQuery query, CancellationToken cancellationToken)
        {
            gate.Entered.TrySetResult();
            await gate.Resume.Task.WaitAsync(cancellationToken);
            if (gate.RefuseAdvice)
                return Result.Failure<PutawayAdvice>(Countflow.Modules.Rules.RulesErrors.NoActiveRuleSet("Putaway"));
            return await inner.SuggestAsync(query, cancellationToken);
        }
    }

    [Fact]
    public async Task An_interrupted_posted_receipt_resumes_only_its_original_body_without_duplicate_sources()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        var gate = new ReceiptAdviceGate { RefuseAdvice = true };
        gate.Resume.TrySetResult();
        using var failingApp = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IPutawayAdvisor>(provider =>
                new PausedReceiptAdvisor(provider.GetRequiredService<PutawayAdvisor>(), gate)))));
        using var receiving = failingApp.CreateClient();
        receiving.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var key = Guid.NewGuid().ToString();
        var body = new { sku = second, quantity = 20m, toLocationCode = _dock, sscc };
        var interrupted = await PostAsync(receiving, "/api/v1/inbound/receipts", body, key);
        Assert.Equal(HttpStatusCode.InternalServerError, interrupted.StatusCode);
        Assert.Equal(20m, await BalanceAsync(second, _dock));
        var changed = await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 21m, toLocationCode = _dock, sscc }, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var claim = await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { });
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        var resumed = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", body, key));
        var replay = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", body, key));
        Assert.Equal(one.WorkTaskId, resumed.WorkTaskId);
        Assert.Equal(resumed.WorkTaskId, replay.WorkTaskId);
        var detail = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{one.WorkTaskId}");
        Assert.Equal(30m, detail.Task.Quantity);
        Assert.Equal(20m, await BalanceAsync(second, _dock));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { })).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reusing_a_receipt_key_cannot_change_between_unplated_and_pallet_stock(bool startsOnPallet)
    {
        var sku = await NewItemAsync(caller: startsOnPallet ? "PalletKeyToUnplated" : "UnplatedKeyToPallet");
        var sscc = NewSscc();
        var key = Guid.NewGuid().ToString();
        await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = _dock, sscc = startsOnPallet ? sscc : null }, key));
        var changed = await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = _dock, sscc = startsOnPallet ? null : sscc }, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(10m, await BalanceAsync(sku, _dock));
        var next = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 20m, toLocationCode = _dock, sscc }));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, $"/api/v1/work/tasks/{next.WorkTaskId}/claim", new { })).StatusCode);
    }

    [Fact]
    public async Task A_lost_preparation_acknowledgement_resumes_before_posting_stock()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();
        var one = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));
        using var failingApp = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IPutawayReceipts>(provider =>
                new LostPreparationAcknowledgement(provider.GetRequiredService<Countflow.Modules.Work.Integration.PutawayReceipts>())))));
        using var receiving = failingApp.CreateClient();
        receiving.DefaultRequestHeaders.Authorization = _operator.DefaultRequestHeaders.Authorization;
        var key = Guid.NewGuid().ToString();
        var body = new { sku = second, quantity = 20m, toLocationCode = _dock, sscc };
        var response = await PostAsync(receiving, "/api/v1/inbound/receipts", body, key);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0m, await BalanceAsync(second, _dock));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(_operator, $"/api/v1/work/tasks/{one.WorkTaskId}/claim", new { })).StatusCode);
        var resumed = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", body, key));
        Assert.Equal(one.WorkTaskId, resumed.WorkTaskId);
        Assert.Equal(20m, await BalanceAsync(second, _dock));
        var completed = await Read<TaskCompletion>(await PostAsync(_operator,
            $"/api/v1/work/tasks/{one.WorkTaskId}/complete", new { toLocationCode = one.SuggestedLocationCode }));
        Assert.Equal(2, completed.Movements.Count);
    }

    private sealed class LostPreparationAcknowledgement(Countflow.Modules.Work.Integration.PutawayReceipts inner) : IPutawayReceipts
    {
        public async Task<Result> PrepareAsync(PutawayReceiptPreparation request, CancellationToken cancellationToken)
        {
            var result = await inner.PrepareAsync(request, cancellationToken);
            if (result.IsSuccess) throw new IOException("Simulated lost preparation acknowledgement");
            return result;
        }
        public Task AbortAsync(string key, CancellationToken cancellationToken) => inner.AbortAsync(key, cancellationToken);
    }

    [Fact]
    public async Task An_assigned_receipt_from_before_the_intent_protocol_still_replays()
    {
        var sku = await NewItemAsync();
        var sscc = NewSscc();
        var key = Guid.NewGuid().ToString();
        Guid taskId;
        using (var scope = fixture.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var item = (await services.GetRequiredService<IItemDirectory>().FindBySkuAsync(sku, CancellationToken.None))!;
            var dock = (await services.GetRequiredService<ILocationDirectory>().FindStorageLocationByCodeAsync(_dock, CancellationToken.None))!;
            var actor = new PostingActor(Guid.NewGuid(), "legacy-receipt");
            var receipt = await services.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction
            {
                ItemId = item.Id, Quantity = 10m, ToLocationId = dock.Id, Sscc = sscc,
                IdempotencyKey = key + ":receipt", Actor = actor
            }, CancellationToken.None);
            Assert.True(receipt.IsSuccess);
            var advice = await services.GetRequiredService<IPutawayAdvisor>().SuggestAsync(new PutawayQuery
            {
                WarehouseId = dock.WarehouseId, FromLocationId = dock.Id, ItemId = item.Id,
                Quantity = 10m, LicensePlateId = receipt.Value.LicensePlateId, Actor = actor
            }, CancellationToken.None);
            Assert.True(advice.IsSuccess);
            var task = await services.GetRequiredService<IWorkQueue>().EnqueueAsync(new WorkTaskRequest
            {
                Kind = WorkTaskKind.Putaway, WarehouseId = dock.WarehouseId, ItemId = item.Id,
                Quantity = 10m, FromLocationId = dock.Id, LicensePlateId = receipt.Value.LicensePlateId,
                SuggestedLocationId = advice.Value.LocationId, DecisionTraceId = advice.Value.DecisionTraceId,
                IdempotencyKey = key + ":putaway"
            }, CancellationToken.None);
            Assert.True(task.IsSuccess);
            taskId = task.Value;
        }
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, $"/api/v1/work/tasks/{taskId}/claim", new { })).StatusCode);
        var replay = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 10m, toLocationCode = _dock, sscc }, key));
        Assert.Equal(taskId, replay.WorkTaskId);
        Assert.Equal(10m, await BalanceAsync(sku, _dock));
        var completed = await PostAsync(_operator, $"/api/v1/work/tasks/{taskId}/complete",
            new { toLocationCode = replay.SuggestedLocationCode });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task A_mixed_pallet_is_one_task_and_moves_in_one_transaction()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var third = await NewItemAsync();
        var sscc = NewSscc();

        // A trailer is unloaded SKU by SKU onto one pallet.
        var one = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));

        var two = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));

        var three = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku = third, quantity = 30m, toLocationCode = _dock, sscc }));

        // One pallet is one thing somebody picks up, so it is one job — otherwise
        // three operators walk to the same pallet and two find it gone.
        Assert.Equal(one.WorkTaskId, two.WorkTaskId);
        Assert.Equal(one.WorkTaskId, three.WorkTaskId);

        var detail = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{one.WorkTaskId}");
        Assert.Equal(60m, detail.Task.Quantity);

        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{one.WorkTaskId}/complete",
            new { toLocationCode = one.SuggestedLocationCode }));

        // Three ledger lines under one confirmation — the composite deferred from
        // Phase 2, with the caller that finally needed it.
        Assert.Equal(3, completed.Movements.Count);
        Assert.All(completed.Movements, m => Assert.False(m.WasAlreadyPosted));

        foreach (var (sku, quantity) in new[] { (first, 10m), (second, 20m), (third, 30m) })
        {
            Assert.Equal(0m, await BalanceAsync(sku, _dock));
            Assert.Equal(quantity, await BalanceAsync(sku, one.SuggestedLocationCode!));

            var putaway = (await MovementsAsync(sku)).Single(m => m.Type == "Putaway");
            Assert.Equal(one.WorkTaskId, putaway.TaskId);
            Assert.Equal(one.DecisionTraceId, putaway.DecisionTraceId);
        }
    }

    [Fact]
    public async Task A_retried_whole_pallet_move_replays_rather_than_finding_the_pallet_gone()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var sscc = NewSscc();

        var one = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku = first, quantity = 7m, toLocationCode = _dock, sscc }));

        await PostAsync(_operator, "/api/v1/inbound/receipts",
            new { sku = second, quantity = 9m, toLocationCode = _dock, sscc });

        var url = $"/api/v1/work/tasks/{one.WorkTaskId}/complete";
        var body = new { toLocationCode = one.SuggestedLocationCode };
        var key = $"plate-{sscc}";

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_operator, url, body, key)).StatusCode);

        // The retry arrives after the pallet has already moved. Reading the
        // source location would find it empty and report an error for work that
        // in fact succeeded, so the key family is recognised first.
        var retry = await PostAsync(_operator, url, body, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        Assert.Equal(7m, await BalanceAsync(first, one.SuggestedLocationCode!));
        Assert.Equal(9m, await BalanceAsync(second, one.SuggestedLocationCode!));
        Assert.Equal(2, (await MovementsAsync(first)).Count);
    }

    // ------------------------------------------------------------ advice notes

    [Fact]
    public async Task Receiving_against_an_advice_note_measures_the_delivery_against_it()
    {
        var sku = await NewItemAsync();
        var reference = NewAsnReference();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/v1/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            supplierName = "Test Supplier BV",
            lines = new[] { new { sku, expectedQuantity = 100m } }
        })).StatusCode);

        var partial = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 60m, toLocationCode = _dock, asnReference = reference }));

        Assert.NotNull(partial.AsnLine);
        Assert.Equal(100m, partial.AsnLine.ExpectedQuantity);
        Assert.Equal(60m, partial.AsnLine.ReceivedQuantity);
        Assert.Equal(40m, partial.AsnLine.OutstandingQuantity);
        Assert.Equal("Receiving", partial.AsnLine.AsnStatus);

        var rest = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku, quantity = 40m, toLocationCode = _dock, asnReference = reference }));

        Assert.Equal(0m, rest.AsnLine!.OutstandingQuantity);
        Assert.Equal("Complete", rest.AsnLine.AsnStatus);
    }

    [Fact]
    public async Task A_retried_dock_scan_does_not_book_the_delivery_in_twice()
    {
        var sku = await NewItemAsync();
        var reference = NewAsnReference();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/v1/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            lines = new[] { new { sku, expectedQuantity = 100m } }
        })).StatusCode);

        var body = new { sku, quantity = 60m, toLocationCode = _dock, asnReference = reference };
        var key = $"dock-{sku}";

        var first = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", body, key));
        var second = await Read<DockReceipt>(await PostAsync(_operator, "/api/v1/inbound/receipts", body, key));

        Assert.False(first.Receipt.WasAlreadyPosted);
        Assert.True(second.Receipt.WasAlreadyPosted);

        // Every step of the loop recognises its own earlier attempt: one ledger
        // entry, one booking against the line, one task.
        Assert.Equal(60m, second.AsnLine!.ReceivedQuantity);
        Assert.Equal(first.WorkTaskId, second.WorkTaskId);
        Assert.Single(await MovementsAsync(sku));
        Assert.Equal(60m, await BalanceAsync(sku, _dock));
    }

    [Fact]
    public async Task A_sku_that_is_not_on_the_advice_note_is_refused_rather_than_quietly_accepted()
    {
        var onNote = await NewItemAsync();
        var notOnNote = await NewItemAsync();
        var reference = NewAsnReference();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/v1/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            lines = new[] { new { sku = onNote, expectedQuantity = 10m } }
        })).StatusCode);

        // The operator believes they are receiving against a document. Booking it
        // outside that document is how a delivery reconciles perfectly while being
        // wrong.
        var response = await PostAsync(
            _operator, "/api/v1/inbound/receipts",
            new { sku = notOnNote, quantity = 10m, toLocationCode = _dock, asnReference = reference });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await MovementsAsync(notOnNote));
    }

    [Fact]
    public async Task Goods_cannot_be_booked_straight_into_racking()
    {
        var sku = await NewItemAsync();
        var bin = await ADifferentAmbientBinAsync(string.Empty);

        // It would conserve stock perfectly, and skip the putaway movement and its
        // decision trace — which is the entire loop this module exists to run.
        var response = await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 10m, toLocationCode = bin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------------- helpers

    private async Task<string> NewItemAsync(
        TemperatureClass temperature = TemperatureClass.Ambient,
        [CallerMemberName] string caller = "")
    {
        var sku = $"INB-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Inbound test item for {caller}",
            BaseUom = "EA",
            TemperatureClass = temperature,
            AbcClass = AbcClass.B
        };

        // Dimensions on file, so the capacity constraints and the cube preference
        // are exercised rather than silently skipped as "unknown".
        item.PackConfigurations.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Each,
            BaseUnitQuantity = 1,
            LengthMm = 100m,
            WidthMm = 100m,
            HeightMm = 100m,
            GrossWeightKg = 0.5m,
            IsPickUnit = true
        });

        masterData.Items.Add(item);
        await masterData.SaveChangesAsync();

        return sku;
    }

    /// <summary>
    /// A reference no other test in this class will produce. The counter alone is
    /// not enough: xUnit builds a fresh instance per test method, so it restarts
    /// at zero every time while the database does not.
    /// </summary>
    private string NewAsnReference([CallerMemberName] string caller = "") =>
        $"ASN-{caller[..Math.Min(caller.Length, 20)]}-{Interlocked.Increment(ref _counter):D2}";

    /// <summary>An ambient storage bin that is not the one passed in.</summary>
    private async Task<string> ADifferentAmbientBinAsync(string other)
    {
        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        return await masterData.Locations
            .Where(l => l.Type == LocationType.Bulk && l.Code.Contains("-AMB-") && l.Code != other)
            .OrderByDescending(l => l.Code)
            .Select(l => l.Code)
            .FirstAsync();
    }

    /// <summary>
    /// A licence plate that survives the same validation a real one does — the
    /// ledger checks the check digit, so a test plate with a hand-made one would
    /// be rejected at the dock. The serial part is random so tests cannot collide
    /// on a pallet.
    /// </summary>
    private static string NewSscc() =>
        Sscc.FromPayload($"3{Random.Shared.NextInt64(0, 9_999_999_999_999_999L):D16}");

    private async Task<decimal> BalanceAsync(string sku, string locationCode)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status=Available");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<List<MovementRow>> MovementsAsync(string sku) =>
        (await GetAsync<MovementPage>($"/api/v1/inventory/movements?sku={Uri.EscapeDataString(sku)}")).Items;

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString());

        return await client.SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _supervisor.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

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

        await EnsureAsync("inbound-supervisor", "INB-SUP", Roles.Supervisor);
        await EnsureAsync("inbound-operator", "INB-OP1", Roles.Operator);
        await EnsureAsync("inbound-operator-2", "INB-OP2", Roles.Operator);

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

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(userName, Secret));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);

        return client;
    }

    // ----------------------------------------------------------- wire shapes

    private sealed record LoginResponse(string AccessToken);

    private sealed record PostedMovementBody(
        Guid MovementId,
        long Sequence,
        decimal Quantity,
        Guid? LotId,
        Guid? LicensePlateId,
        bool WasAlreadyPosted);

    private sealed record AsnLineProgressBody(
        string Reference,
        int LineNumber,
        decimal ExpectedQuantity,
        decimal ReceivedQuantity,
        decimal OutstandingQuantity,
        string AsnStatus);

    private sealed record DockReceipt(
        PostedMovementBody Receipt,
        string Sku,
        string ToLocationCode,
        string? LotNumber,
        string? Sscc,
        Guid? WorkTaskId,
        string? SuggestedLocationCode,
        Guid DecisionTraceId,
        int ConsideredCount,
        int AdmissibleCount,
        AsnLineProgressBody? AsnLine);

    private sealed record TaskBody(
        Guid Id,
        string Kind,
        string Status,
        string Sku,
        decimal Quantity,
        string? Sscc,
        string FromLocationCode,
        string? SuggestedLocationCode,
        Guid? DecisionTraceId,
        string? CompletedAtLocationCode,
        bool WasOverridden,
        string? OverrideReason);

    private sealed record NoteBody(string Code, Dictionary<string, string> Args);

    private sealed record ScoreLineBody(string RuleCode, decimal Weight, decimal Value, NoteBody Reason);

    private sealed record TracedCandidateBody(
        string CandidateKey,
        int Rank,
        bool IsAdmissible,
        decimal Score,
        NoteBody? ExcludedBy,
        List<ScoreLineBody> Scores);

    private sealed record TraceView(
        Guid Id,
        string RuleSetName,
        int RuleSetVersion,
        int ConsideredCount,
        int AdmissibleCount,
        string? SelectedKey,
        List<TracedCandidateBody> Candidates);

    private sealed record TaskDetail(TaskBody Task, TraceView? Decision);

    private sealed record TaskCompletion(TaskBody Task, List<PostedMovementBody> Movements);

    private sealed record PreviewBody(string? LocationCode, decimal Score, TraceView Trace);

    private sealed record BalancePage(List<BalanceRow> Items);

    private sealed record BalanceRow(string LocationCode, decimal Quantity);

    private sealed record MovementPage(List<MovementRow> Items, int Total);

    private sealed record MovementRow(
        Guid Id,
        long Sequence,
        string Sku,
        decimal Quantity,
        string FromLocationCode,
        string ToLocationCode,
        string Type,
        Guid? TaskId,
        Guid? DecisionTraceId);
}
