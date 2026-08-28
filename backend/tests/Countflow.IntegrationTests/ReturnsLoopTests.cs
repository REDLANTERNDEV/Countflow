using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Phase 7 end to end: goods come back, somebody opens the carton, and the fourth
/// vocabulary decides what becomes of them.
/// <para>
/// The first test proves the sentence the phase is for — despatch, return,
/// inspection, disposition, and a ledger that still sums to zero over the whole
/// round trip. Everything after it defends the properties that make it worth
/// anything: that a return credits the account the despatch debited, that a
/// four-element field does not fall into the normalisation trap the invariants
/// describe, that the one irreversible option is guarded twice, and that an
/// override is recorded next to what it overrode.
/// </para>
/// </summary>
public sealed class ReturnsLoopTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private string _warehouseCode = string.Empty;
    private string _dock = string.Empty;
    private string _bench = string.Empty;
    private string _returnsBay = string.Empty;
    private string _customerAccount = string.Empty;
    private string _supplierAccount = string.Empty;
    private string _transitAccount = string.Empty;
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

            _dock = await CodeOfAsync(masterData, LocationType.ReceivingDock);
            _bench = await CodeOfAsync(masterData, LocationType.Packing);
            _returnsBay = await CodeOfAsync(masterData, LocationType.Quarantine);
            _customerAccount = await CodeOfAsync(masterData, LocationType.Customer);
            _supplierAccount = await CodeOfAsync(masterData, LocationType.Supplier);
            _transitAccount = await CodeOfAsync(masterData, LocationType.InTransit);

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("returns-supervisor");
        _operator = await SignInAsync("returns-operator");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --------------------------------------------------------------- the loop

    [Fact]
    public async Task Goods_come_back_are_inspected_restocked_and_the_ledger_still_sums_to_zero()
    {
        var sku = await NewItemAsync();
        var order = await DespatchOrderAsync(sku, 40m);

        // 1. Booked into the returns bay. Held, whatever the desk thinks of the
        //    carton — what the goods actually are is the inspection's answer.
        var booked = await Read<BookReturnResult>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/returns",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewReference(),
                toLocationCode = _returnsBay,
                orderReference = order.Reference,
                lines = new[] { new { sku, quantity = 12m } }
            }));

        Assert.Equal("Received", booked.Return.Status);
        Assert.Equal(1, booked.InspectionsRaised);
        Assert.Single(booked.Movements);

        var line = booked.Return.Lines.Single();
        Assert.Equal(12m, line.Quantity);
        Assert.Equal(0m, line.DispositionedQuantity);
        Assert.Equal(12m, line.OutstandingQuantity);
        Assert.NotNull(line.SalesOrderLineId);
        Assert.NotNull(line.InspectionTaskId);

        // Held stock, not sellable stock. The point of booking it this way.
        Assert.Equal(12m, await BalanceAsync(sku, _returnsBay, "OnHold"));
        Assert.Equal(0m, await BalanceAsync(sku, _returnsBay, "Available"));

        // 2. The order knows what came back, and knows it separately from what it
        //    shipped. A return is new supply, not an un-picking.
        var afterReturn = await GetAsync<OrderBody>($"/api/v1/outbound/orders/{order.Id}");
        var orderLine = afterReturn.Lines.Single();
        Assert.Equal(12m, orderLine.ReturnedQuantity);
        Assert.Equal(40m, orderLine.AllocatedQuantity);
        Assert.Equal("Despatched", afterReturn.Status);

        // 3. The inspector opens the carton and the rules decide. Sealed, in
        //    condition, straight back — restock is the obvious answer and the trace
        //    is the argument for it.
        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            $"/api/v1/outbound/returns/{booked.Return.Id}/lines/{line.Id}/disposition",
            new { condition = "Available", isSealed = true }));

        Assert.Equal("Restock", decided.Applied);
        Assert.Equal("Restock", decided.AdvisedDisposition);
        Assert.False(decided.WasOverridden);
        Assert.NotEqual(Guid.Empty, decided.DecisionTraceId);
        Assert.Equal(4, decided.ConsideredCount);
        Assert.NotEmpty(decided.Reasons);
        Assert.All(decided.Reasons, r => Assert.StartsWith("rules.disposition.", r.Reason.Code));

        // A restock is two decisions and two traces, which is the honest count:
        // "should this go back on a shelf" and "which shelf" are different
        // questions with different rule sets.
        Assert.NotNull(decided.WorkTaskId);
        Assert.NotNull(decided.SuggestedLocationCode);

        Assert.Equal("Dispositioned", decided.Return.Status);
        Assert.Equal(12m, decided.Return.Lines.Single().DispositionedQuantity);

        // Released to Available, still standing in the bay until somebody moves it.
        Assert.Equal(12m, await BalanceAsync(sku, _returnsBay, "Available"));
        Assert.Equal(0m, await BalanceAsync(sku, _returnsBay, "OnHold"));

        // 4. The putaway is an ordinary job on the ordinary queue, and it explains
        //    itself the way a dock receipt's does.
        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{decided.WorkTaskId}/claim", new { }));

        Assert.Equal("Putaway", claimed.Task.Kind);
        Assert.Equal(_returnsBay, claimed.Task.FromLocationCode);
        Assert.Equal("Putaway", claimed.Decision!.Kind);

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{claimed.Task.Id}/complete",
            new { toLocationCode = decided.SuggestedLocationCode }));

        Assert.Equal(12m, await BalanceAsync(sku, decided.SuggestedLocationCode!, "Available"));

        // 5. Nothing was created and nothing destroyed, across a round trip that
        //    now goes out to the customer and partly comes back. That sum is the
        //    whole claim ADR 0001 makes, and it is the reason the return had to
        //    credit Customer rather than Supplier.
        Assert.Equal(0m, await EveryHoldingAsync(sku));

        var movements = await MovementsAsync(sku);
        Assert.Contains(movements, m => m.Type == "Receipt" && m.FromLocationCode == _customerAccount);
        Assert.Contains(movements, m => m.Type == "StatusChange");
    }

    // ------------------------------------------- the counterparty correction

    [Fact]
    public async Task A_return_credits_the_customer_account_the_despatch_debited()
    {
        var sku = await NewItemAsync();
        var order = await DespatchOrderAsync(sku, 20m);

        await BookAsync(sku, 20m, orderReference: order.Reference);

        var movements = await MovementsAsync(sku);

        var despatch = movements.Single(m => m.Type == "Despatch");
        var returned = movements.Single(m => m.Type == "Receipt" && m.FromLocationCode == _customerAccount);

        // Out to Customer and back from Customer. Before Phase 7 the return
        // credited Supplier, so the customer account only ever grew and the one
        // account a distribution centre most wants to reconcile could not be.
        Assert.Equal(_customerAccount, despatch.ToLocationCode);
        Assert.Equal(_customerAccount, returned.FromLocationCode);

        // The purchase receipt that stocked the shelf still comes from Supplier.
        // Widening the shape rule did not blur the two.
        Assert.Contains(movements, m => m.Type == "Receipt" && m.FromLocationCode == _supplierAccount);

        // The customer account nets to zero once everything comes back, which it
        // could not do while returns landed on the supplier's side of the book.
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(_customerAccount)}&includeEmpty=true");

        Assert.Equal(0m, page.Items.Sum(r => r.Quantity));
    }

    // ------------------------------------------------- the constraints bite

    [Fact]
    public async Task An_opened_carton_is_not_restocked_and_the_trace_says_which_rule_refused_it()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 6m);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Available", isSealed = false }));

        Assert.NotEqual("Restock", decided.Applied);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{decided.DecisionTraceId}");

        // Four candidates always, however few survive. Pre-filtering the field to
        // the plausible ones would be the WHERE clause the location directory
        // refuses to be: a candidate excluded by a query never appears in a trace,
        // and "why wasn't it just put back?" is what this decision exists to answer.
        Assert.Equal(4, trace.ConsideredCount);
        Assert.Equal(4, trace.Candidates.Count);

        var restock = trace.Candidates.Single(c => c.CandidateKey == "restock");
        Assert.False(restock.IsAdmissible);
        Assert.Equal("rules.disposition.packaging_opened", restock.ExcludedBy!.Code);
    }

    [Fact]
    public async Task Sealed_stock_that_is_still_in_date_is_never_ranked_for_destruction()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 6m);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor, LineUrl(booked), new { condition = "Available", isSealed = true }));

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{decided.DecisionTraceId}");

        // The safety catch on the one irreversible option. Scrapping is
        // operationally the cheapest thing a warehouse can do with an awkward
        // return, so a weight set badly enough would eventually rank it top for a
        // sealed, in-date carton — and that mistake is a pallet in a skip rather
        // than something a re-run fixes.
        var scrap = trace.Candidates.Single(c => c.CandidateKey == "scrap");
        Assert.False(scrap.IsAdmissible);
        Assert.Equal("rules.disposition.still_saleable", scrap.ExcludedBy!.Code);
    }

    [Fact]
    public async Task Destroying_returned_stock_has_to_say_why()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 6m);

        var refused = await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Damaged", disposition = "Scrap" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("outbound.scrap_needs_notes", problem!.Code);

        // With a reason, it goes — and the reason travels onto the movement,
        // because a write-off is explained only by the person who ordered it.
        var scrapped = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Damaged", disposition = "Scrap", notes = "Crushed in transit; unsaleable." }));

        Assert.Equal("Scrap", scrapped.Applied);
        Assert.True(scrapped.WasOverridden || scrapped.AdvisedDisposition == "Scrap");
        Assert.Contains(await MovementsAsync(sku), m => m.Type == "Scrap");
    }

    // --------------------------------------- the four-element field is honest

    [Fact]
    public async Task A_narrow_field_is_scored_on_absolute_scales_rather_than_stretched_across_itself()
    {
        // Damaged, opened and not worth repairing: restock and repair are both
        // refused, and the field collapses to two. This is the shape the invariants
        // warn about from the other side — a narrow field stretched to everything —
        // and it is the reason every preference in this vocabulary scores against a
        // scale that exists without the field.
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 4m);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Damaged", isSealed = false, notes = "Split container." }));

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{decided.DecisionTraceId}");

        var restock = trace.Candidates.Single(c => c.CandidateKey == "restock");
        Assert.False(restock.IsAdmissible);
        Assert.Equal("rules.disposition.not_good_stock", restock.ExcludedBy!.Code);

        var repair = trace.Candidates.Single(c => c.CandidateKey == "repair");
        Assert.False(repair.IsAdmissible);
        Assert.Equal("rules.disposition.not_repairable", repair.ExcludedBy!.Code);

        var admissible = trace.Candidates.Where(c => c.IsAdmissible).ToList();
        Assert.Equal(2, admissible.Count);

        // The assertion that matters. A field-relative normaliser puts its best
        // candidate at exactly 1 and its worst at exactly 0 however alike the two
        // really are — that is its fingerprint, and over a field this narrow it
        // would be the fingerprint on every decision ever made. These scores are
        // sums of fixed values instead, so neither end of the field is pinned.
        foreach (var candidate in admissible)
        {
            Assert.True(
                candidate.Score is > 0m and < 1m,
                $"{candidate.CandidateKey} scored {candidate.Score}, which is a field-relative extreme");
        }

        // And the same rule, asked about the same two options, does not report them
        // as opposites. `disposition.recover_value` scores quarantine 0.3 and scrap
        // 0.0 whether it is looking at four candidates or two.
        var value = admissible
            .Select(c => c.Scores.Single(s => s.RuleCode == "disposition.recover_value").Value)
            .ToList();

        Assert.Contains(0.3m, value);
        Assert.Contains(0m, value);
    }

    // ------------------------------------------------------------ overrides

    [Fact]
    public async Task An_override_records_what_the_rules_would_have_done()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 10m);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Available", isSealed = true, disposition = "Quarantine", notes = "Customer disputes it." }));

        Assert.Equal("Quarantine", decided.Applied);
        Assert.Equal("Restock", decided.AdvisedDisposition);
        Assert.True(decided.WasOverridden);

        // The rules still ran and still wrote their trace. An override that skipped
        // the evaluation would destroy the only record of what the system would
        // have done, which is the one thing that makes an override reviewable.
        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{decided.DecisionTraceId}");
        Assert.Equal("restock", trace.SelectedKey);
        Assert.Equal("Disposition", trace.Kind);

        var stored = decided.Return.Lines.Single().Dispositions.Single();
        Assert.True(stored.WasOverridden);
        Assert.Equal(decided.DecisionTraceId, stored.DecisionTraceId);

        Assert.Equal(10m, await BalanceAsync(sku, _returnsBay, "Quarantine"));
    }

    // ------------------------------------------------- repair is not despatch

    [Fact]
    public async Task Goods_sent_for_repair_go_to_in_transit_rather_than_to_the_customer()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 5m);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new
            {
                condition = "Damaged",
                isRepairable = true,
                disposition = "Repair",
                notes = "To Acme Repairs, cracked housing."
            }));

        Assert.Equal("Repair", decided.Applied);

        // A transfer, not a despatch, and the difference is the point: goods away
        // for repair are still the company's, still expected, and still owed a
        // receipt. A despatch would settle them against a customer who never
        // bought them.
        var movement = (await MovementsAsync(sku)).Single(m => m.ToLocationCode == _transitAccount);
        Assert.Equal("Transfer", movement.Type);
        Assert.Equal(decided.DecisionTraceId, movement.DecisionTraceId);

        Assert.Equal(0m, await BalanceAsync(sku, _returnsBay, "OnHold"));
    }

    // ------------------------------------------------------------ retries

    [Fact]
    public async Task One_disposition_submitted_twice_is_one_write_off()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 8m);

        var key = Guid.CreateVersion7().ToString();
        var body = new { condition = "Damaged", disposition = "Scrap", notes = "Water damage." };

        var first = await Read<DispositionResult>(await PostAsync(_supervisor, LineUrl(booked), body, key));
        var second = await Read<DispositionResult>(await PostAsync(_supervisor, LineUrl(booked), body, key));

        Assert.Equal(first.DecisionTraceId, second.DecisionTraceId);
        Assert.True(second.Movement.WasAlreadyPosted);

        // One row and one movement. A retried scanner must not destroy the stock
        // twice, and the line must not close twice either.
        Assert.Single(second.Return.Lines.Single().Dispositions);
        Assert.Single(await MovementsAsync(sku), m => m.Type == "Scrap");
        Assert.Equal(8m, second.Return.Lines.Single().DispositionedQuantity);
    }

    [Fact]
    public async Task A_line_can_be_split_between_two_dispositions()
    {
        var sku = await NewItemAsync();
        var booked = await BookAsync(sku, 10m);

        var restocked = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Available", isSealed = true, quantity = 6m }));

        Assert.Equal("Restock", restocked.Applied);
        Assert.Equal("Received", restocked.Return.Status);
        Assert.Equal(4m, restocked.Return.Lines.Single().OutstandingQuantity);

        var scrapped = await Read<DispositionResult>(await PostAsync(
            _supervisor,
            LineUrl(booked),
            new { condition = "Damaged", disposition = "Scrap", quantity = 4m, notes = "Two cases crushed." }));

        // Six back on the shelf and four in the skip is a completely ordinary
        // outcome, and it is two append-only rows rather than one number edited
        // twice.
        Assert.Equal("Dispositioned", scrapped.Return.Status);
        Assert.Equal(0m, scrapped.Return.Lines.Single().OutstandingQuantity);
        Assert.Equal(2, scrapped.Return.Lines.Single().Dispositions.Count);

        var refused = await PostAsync(
            _supervisor, LineUrl(booked), new { condition = "Damaged", disposition = "Scrap", notes = "Again." });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ------------------------------------------------------ where it may land

    [Fact]
    public async Task Returned_goods_cannot_be_booked_straight_into_racking()
    {
        var sku = await NewItemAsync();

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var rack = await CodeOfAsync(masterData, LocationType.Bulk);

        var refused = await PostAsync(
            _supervisor,
            "/api/v1/outbound/returns",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewReference(),
                toLocationCode = rack,
                lines = new[] { new { sku, quantity = 3m } }
            });

        // Stock nobody has inspected must not land where the next allocation would
        // find it. The condition alone would not save it — a bay that holds
        // sellable stock is a bay somebody eventually releases this into by hand.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("outbound.not_a_returns_location", problem!.Code);
    }

    [Fact]
    public async Task A_return_with_no_order_behind_it_is_booked_rather_than_refused()
    {
        var sku = await NewItemAsync();

        var booked = await BookAsync(sku, 7m);

        // The mirror of the blind receipt the dock has always accepted. Refusing it
        // would leave the goods in a corner, unbooked and invisible.
        Assert.Null(booked.Return.SalesOrderId);
        Assert.Null(booked.Return.Lines.Single().SalesOrderLineId);

        var decided = await Read<DispositionResult>(await PostAsync(
            _supervisor, LineUrl(booked), new { condition = "Available", isSealed = true }));

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{decided.DecisionTraceId}");
        var restock = trace.Candidates.Single(c => c.CandidateKey == "restock");

        // Nobody knows how long the customer had it, so freshness scores the
        // midpoint rather than either end — "assume it is fresh" and "assume it is
        // ancient" are both inventions the trace would have to defend.
        var freshness = restock.Scores.Single(s => s.RuleCode == "disposition.return_freshness");
        Assert.Equal(0.5m, freshness.Value);
        Assert.Equal("rules.disposition.no_despatch_on_record", freshness.Reason.Code);
    }

    // ------------------------------------------------------------- helpers

    private static string LineUrl(BookReturnResult booked) =>
        $"/api/v1/outbound/returns/{booked.Return.Id}/lines/{booked.Return.Lines[0].Id}/disposition";

    private async Task<BookReturnResult> BookAsync(
        string sku,
        decimal quantity,
        string? orderReference = null,
        [CallerMemberName] string caller = "") =>
        await Read<BookReturnResult>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/returns",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewReference(caller),
                toLocationCode = _returnsBay,
                orderReference,
                lines = new[] { new { sku, quantity } }
            }));

    /// <summary>
    /// A whole outbound round trip, so there is something to send back. Everything
    /// here is Phase 3 and Phase 4 machinery that returns does not touch.
    /// </summary>
    private async Task<OrderBody> DespatchOrderAsync(string sku, decimal quantity)
    {
        var bin = await ReceiveAndPutAwayAsync(sku, quantity);

        var order = await Read<OrderBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewReference(),
                stagingLocationCode = _bench,
                lines = new[] { new { sku, quantity } }
            }));

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        var allocation = released.Order.Lines.Single().Allocations.Single();
        Assert.Equal(bin, allocation.LocationCode);

        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/claim", new { }));

        await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{claimed.Task.Id}/complete", new { toLocationCode = _bench }));

        await Read<OrderBody>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/pack", new { }));

        var despatched = await Read<DespatchResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/despatch", new { }));

        return despatched.Order;
    }

    private async Task<string> ReceiveAndPutAwayAsync(string sku, decimal quantity)
    {
        var receipt = await Read<DockReceipt>(await PostAsync(
            _supervisor,
            "/api/v1/inbound/receipts",
            new { sku, quantity, toLocationCode = _dock }));

        Assert.NotNull(receipt.SuggestedLocationCode);

        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{receipt.WorkTaskId}/claim", new { }));

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{claimed.Task.Id}/complete",
            new { toLocationCode = receipt.SuggestedLocationCode }));

        return receipt.SuggestedLocationCode!;
    }

    private static async Task<string> CodeOfAsync(MasterDataDbContext masterData, LocationType type) =>
        await masterData.Locations
            .Where(l => l.Type == type)
            .OrderBy(l => l.Code)
            .Select(l => l.Code)
            .FirstAsync();

    /// <summary>
    /// A reference no other test in this class will produce.
    /// <para>
    /// Neither the counter nor the caller name is enough on its own, and finding
    /// that out cost a run. xUnit builds a fresh instance per test method, so the
    /// counter restarts at zero every time while the database does not — and the
    /// caller name collapses to the *helper's* name for every reference minted
    /// inside <see cref="DespatchOrderAsync"/>, so two tests that both despatch an
    /// order collide on "RTN-DespatchOrderAsync-01". The suffix is what makes it
    /// unique; the caller name is kept only because a failing test is easier to
    /// place when its rows say which test wrote them.
    /// </para>
    /// </summary>
    private string NewReference([CallerMemberName] string caller = "") =>
        $"RTN-{caller[..Math.Min(caller.Length, 24)]}-{Interlocked.Increment(ref _counter):D2}-" +
        $"{Guid.CreateVersion7().ToString()[^8..]}";

    private async Task<string> NewItemAsync(
        bool hazardous = false,
        bool expiryTracked = false,
        int? shelfLifeDays = null,
        int? minimumRemainingPercent = null,
        [CallerMemberName] string caller = "")
    {
        var sku = $"RTN-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Returns test item for {caller}",
            BaseUom = "EA",
            AbcClass = AbcClass.B,
            IsLotTracked = expiryTracked,
            IsExpiryTracked = expiryTracked,
            IsHazardous = hazardous,
            ShelfLifeDays = shelfLifeDays,
            MinimumRemainingShelfLifePercent = minimumRemainingPercent
        };

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

    private async Task<decimal> BalanceAsync(string sku, string locationCode, string status)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status={status}");

        return page.Items.Sum(r => r.Quantity);
    }

    /// <summary>
    /// Every holding of the SKU anywhere, physical and virtual, summed. Zero is the
    /// double-entry claim: stock was neither created nor destroyed.
    /// </summary>
    private async Task<decimal> EveryHoldingAsync(string sku)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&includeEmpty=true&pageSize=500");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<List<MovementRow>> MovementsAsync(string sku) =>
        (await GetAsync<MovementPage>(
            $"/api/v1/inventory/movements?sku={Uri.EscapeDataString(sku)}&pageSize=100")).Items;

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

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode} for {url}: " +
            $"{await response.Content.ReadAsStringAsync()}");

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

        await EnsureAsync("returns-supervisor", "RTN-SUP", Roles.Supervisor);
        await EnsureAsync("returns-operator", "RTN-OP1", Roles.Operator);

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

    // ------------------------------------------------------------ wire shapes

    private sealed record LoginResponse(string AccessToken);

    private sealed record ProblemBody(string Code);

    private sealed record PostedMovementBody(
        Guid MovementId, long Sequence, decimal Quantity, bool WasAlreadyPosted);

    private sealed record DockReceipt(
        string Sku, string? SuggestedLocationCode, Guid? WorkTaskId, Guid DecisionTraceId);

    private sealed record DispositionRow(
        Guid Id,
        string Disposition,
        decimal Quantity,
        Guid DecisionTraceId,
        bool WasOverridden,
        Guid MovementId,
        Guid? WorkTaskId);

    private sealed record ReturnLineBody(
        Guid Id,
        int LineNumber,
        string Sku,
        decimal Quantity,
        decimal DispositionedQuantity,
        decimal OutstandingQuantity,
        Guid? SalesOrderLineId,
        Guid? InspectionTaskId,
        string? InspectionState,
        List<DispositionRow> Dispositions);

    private sealed record ReturnBody(
        Guid Id,
        string Reference,
        string Status,
        string? SalesOrderReference,
        Guid? SalesOrderId,
        string ReceivedAtLocationCode,
        List<ReturnLineBody> Lines);

    private sealed record BookReturnResult(
        ReturnBody Return, List<PostedMovementBody> Movements, int InspectionsRaised);

    private sealed record DispositionResult(
        ReturnBody Return,
        string Applied,
        string? AdvisedDisposition,
        bool WasOverridden,
        Guid DecisionTraceId,
        List<ScoreLineBody> Reasons,
        int ConsideredCount,
        int AdmissibleCount,
        PostedMovementBody Movement,
        Guid? WorkTaskId,
        string? SuggestedLocationCode);

    private sealed record AllocationBody(
        Guid Id, decimal Quantity, string LocationCode, Guid DecisionTraceId, Guid? WorkTaskId);

    private sealed record OrderLineBody(
        int LineNumber,
        string Sku,
        decimal OrderedQuantity,
        decimal AllocatedQuantity,
        decimal PickedQuantity,
        decimal ReturnedQuantity,
        List<AllocationBody> Allocations);

    private sealed record OrderBody(Guid Id, string Reference, string Status, List<OrderLineBody> Lines);

    private sealed record ReleaseResult(OrderBody Order, int AllocationsRaised, int TasksRaised);

    private sealed record DespatchResult(OrderBody Order, List<PostedMovementBody> Movements);

    private sealed record TaskBody(
        Guid Id, string Kind, string Status, decimal Quantity, string FromLocationCode);

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
        string Kind,
        string RuleSetName,
        int ConsideredCount,
        int AdmissibleCount,
        string? SelectedKey,
        List<TracedCandidateBody> Candidates);

    private sealed record TaskDetail(TaskBody Task, TraceView? Decision);

    private sealed record TaskCompletion(TaskBody Task, List<PostedMovementBody> Movements);

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
