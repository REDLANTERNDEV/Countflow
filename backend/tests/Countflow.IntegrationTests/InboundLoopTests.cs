using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
public sealed class InboundLoopTests(CountflowApiFixture fixture)
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 48m, toLocationCode = _dock, sscc }));

        Assert.Equal(_dock, received.ToLocationCode);
        Assert.NotEqual(Guid.Empty, received.DecisionTraceId);

        // 2. The rules chose somewhere, out of a field of real candidates.
        Assert.NotNull(received.SuggestedLocationCode);
        Assert.True(received.ConsideredCount > 100, $"only {received.ConsideredCount} candidates considered");
        Assert.True(received.AdmissibleCount > 0);
        Assert.NotNull(received.WorkTaskId);

        // 3. The operator takes the job and sees why that bin.
        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/work/tasks/{received.WorkTaskId}/claim", new { }));

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
            $"/api/work/tasks/{claimed.Task.Id}/complete",
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 10m, toLocationCode = _dock }));

        var second = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts", new { sku, quantity = 10m, toLocationCode = _dock }));

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
            _operator, "/api/inbound/receipts", new { sku, quantity = 12m, toLocationCode = _dock }));

        Assert.NotNull(received.SuggestedLocationCode);
        Assert.Contains("-CHL-", received.SuggestedLocationCode);

        var trace = await GetAsync<TraceView>($"/api/rules/traces/{received.DecisionTraceId}");

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
            _operator, "/api/inbound/receipts", new { sku, quantity = 6m, toLocationCode = _dock }));

        Assert.Null(received.SuggestedLocationCode);
        Assert.Equal(0, received.AdmissibleCount);

        // The pallet is on the dock either way, so the work is real. What the
        // system owes the operator is the reason, not silence.
        Assert.NotNull(received.WorkTaskId);
        Assert.NotEqual(Guid.Empty, received.DecisionTraceId);

        var trace = await GetAsync<TraceView>($"/api/rules/traces/{received.DecisionTraceId}");

        Assert.True(trace.ConsideredCount > 100);
        Assert.Equal(0, trace.AdmissibleCount);
        Assert.All(trace.Candidates, c => Assert.NotNull(c.ExcludedBy));
    }

    [Fact]
    public async Task A_preview_answers_the_same_question_without_moving_anything()
    {
        var sku = await NewItemAsync();

        var preview = await Read<PreviewBody>(await PostAsync(
            _supervisor, "/api/rules/putaway/preview",
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 5m, toLocationCode = _dock }));

        // Rule sets are configuration. Traces are the explanation an operator is
        // owed at the bin — hiding them behind a supervisor role would ship the
        // feature and lose the benefit (ADR 0004).
        Assert.Equal(HttpStatusCode.Forbidden, (await _operator.GetAsync("/api/rules/sets")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _operator.GetAsync($"/api/rules/traces/{received.DecisionTraceId}")).StatusCode);
    }

    // ----------------------------------------------------------- the operator

    [Fact]
    public async Task Confirming_somewhere_else_is_recorded_as_an_override_rather_than_refused()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts", new { sku, quantity = 20m, toLocationCode = _dock }));

        var elsewhere = await ADifferentAmbientBinAsync(received.SuggestedLocationCode!);

        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/work/tasks/{received.WorkTaskId}/complete",
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 30m, toLocationCode = _dock }));

        var url = $"/api/work/tasks/{received.WorkTaskId}/complete";
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 30m, toLocationCode = _dock }));

        var url = $"/api/work/tasks/{received.WorkTaskId}/complete";
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

            await PostAsync(_operator, "/api/inbound/receipts",
                new { sku, quantity = 4m, toLocationCode = _dock });
        }

        // Both press "next job" at the same moment, which is what a shift start
        // actually looks like.
        var claims = await Task.WhenAll(
            Enumerable.Range(0, 3).SelectMany(_ => new[]
            {
                PostAsync(_operator, "/api/work/tasks/claim-next", new { }),
                PostAsync(_secondOperator, "/api/work/tasks/claim-next", new { })
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 8m, toLocationCode = _dock }));

        var release = await PostAsync(_operator, $"/api/work/tasks/{received.WorkTaskId}/release", new { });
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);

        var claimed = await Read<TaskDetail>(await PostAsync(_operator, "/api/work/tasks/claim-next", new { }));

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostAsync(_operator, $"/api/work/tasks/{claimed.Task.Id}/release", new { })).StatusCode);

        var detail = await GetAsync<TaskDetail>($"/api/work/tasks/{claimed.Task.Id}");
        Assert.Equal("Ready", detail.Task.Status);
    }

    [Fact]
    public async Task One_operators_task_cannot_be_completed_by_another()
    {
        var sku = await NewItemAsync();

        var received = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts", new { sku, quantity = 8m, toLocationCode = _dock }));

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync(_operator, $"/api/work/tasks/{received.WorkTaskId}/claim", new { })).StatusCode);

        var stolen = await PostAsync(
            _secondOperator,
            $"/api/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = received.SuggestedLocationCode });

        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

        // And a second operator cannot take it out from under the first either.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await PostAsync(_secondOperator, $"/api/work/tasks/{received.WorkTaskId}/claim", new { })).StatusCode);
    }

    // ---------------------------------------------------------- whole pallets

    [Fact]
    public async Task A_mixed_pallet_is_one_task_and_moves_in_one_transaction()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();
        var third = await NewItemAsync();
        var sscc = NewSscc();

        // A trailer is unloaded SKU by SKU onto one pallet.
        var one = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts",
            new { sku = first, quantity = 10m, toLocationCode = _dock, sscc }));

        var two = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts",
            new { sku = second, quantity = 20m, toLocationCode = _dock, sscc }));

        var three = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts",
            new { sku = third, quantity = 30m, toLocationCode = _dock, sscc }));

        // One pallet is one thing somebody picks up, so it is one job — otherwise
        // three operators walk to the same pallet and two find it gone.
        Assert.Equal(one.WorkTaskId, two.WorkTaskId);
        Assert.Equal(one.WorkTaskId, three.WorkTaskId);

        var detail = await GetAsync<TaskDetail>($"/api/work/tasks/{one.WorkTaskId}");
        Assert.Equal(60m, detail.Task.Quantity);

        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/work/tasks/{one.WorkTaskId}/complete",
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
            _operator, "/api/inbound/receipts",
            new { sku = first, quantity = 7m, toLocationCode = _dock, sscc }));

        await PostAsync(_operator, "/api/inbound/receipts",
            new { sku = second, quantity = 9m, toLocationCode = _dock, sscc });

        var url = $"/api/work/tasks/{one.WorkTaskId}/complete";
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

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            supplierName = "Test Supplier BV",
            lines = new[] { new { sku, expectedQuantity = 100m } }
        })).StatusCode);

        var partial = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts",
            new { sku, quantity = 60m, toLocationCode = _dock, asnReference = reference }));

        Assert.NotNull(partial.AsnLine);
        Assert.Equal(100m, partial.AsnLine.ExpectedQuantity);
        Assert.Equal(60m, partial.AsnLine.ReceivedQuantity);
        Assert.Equal(40m, partial.AsnLine.OutstandingQuantity);
        Assert.Equal("Receiving", partial.AsnLine.AsnStatus);

        var rest = await Read<DockReceipt>(await PostAsync(
            _operator, "/api/inbound/receipts",
            new { sku, quantity = 40m, toLocationCode = _dock, asnReference = reference }));

        Assert.Equal(0m, rest.AsnLine!.OutstandingQuantity);
        Assert.Equal("Complete", rest.AsnLine.AsnStatus);
    }

    [Fact]
    public async Task A_retried_dock_scan_does_not_book_the_delivery_in_twice()
    {
        var sku = await NewItemAsync();
        var reference = NewAsnReference();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            lines = new[] { new { sku, expectedQuantity = 100m } }
        })).StatusCode);

        var body = new { sku, quantity = 60m, toLocationCode = _dock, asnReference = reference };
        var key = $"dock-{sku}";

        var first = await Read<DockReceipt>(await PostAsync(_operator, "/api/inbound/receipts", body, key));
        var second = await Read<DockReceipt>(await PostAsync(_operator, "/api/inbound/receipts", body, key));

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

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(_supervisor, "/api/inbound/asns", new
        {
            reference,
            warehouseCode = _warehouseCode,
            lines = new[] { new { sku = onNote, expectedQuantity = 10m } }
        })).StatusCode);

        // The operator believes they are receiving against a document. Booking it
        // outside that document is how a delivery reconciles perfectly while being
        // wrong.
        var response = await PostAsync(
            _operator, "/api/inbound/receipts",
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
            _operator, "/api/inbound/receipts", new { sku, quantity = 10m, toLocationCode = bin });

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
            $"/api/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status=Available");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<List<MovementRow>> MovementsAsync(string sku) =>
        (await GetAsync<MovementPage>($"/api/inventory/movements?sku={Uri.EscapeDataString(sku)}")).Items;

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

        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(userName, Secret));
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
