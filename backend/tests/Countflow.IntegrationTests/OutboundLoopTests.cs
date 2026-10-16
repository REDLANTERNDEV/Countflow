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
/// Phase 4 end to end: an order is released, allocated by rule, picked on the
/// handheld against a directed pick list, packed and despatched — and the ledger
/// balances to zero across the whole round trip from Supplier to Customer.
/// <para>
/// The first test proves that sentence, which is the phase's definition of done.
/// Everything after it defends the properties that make it worth anything: that a
/// balance is on-hand and not available-to-promise, that the rules can say why
/// they refused a batch, that a commitment is discharged exactly once however many
/// times a scanner retries, and that a cancellation gives the stock back.
/// </para>
/// </summary>
public sealed partial class OutboundLoopTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private HttpClient _secondOperator = null!;
    private string _warehouseCode = string.Empty;
    private string _dock = string.Empty;
    private string _bench = string.Empty;
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

            _bench = await masterData.Locations
                .Where(l => l.Type == LocationType.Packing)
                .OrderBy(l => l.Code)
                .Select(l => l.Code)
                .FirstAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("outbound-supervisor");
        _operator = await SignInAsync("outbound-operator");
        _secondOperator = await SignInAsync("outbound-operator-2");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --------------------------------------------------------------- the loop

    [Fact]
    public async Task An_order_is_released_allocated_picked_packed_and_despatched_and_the_ledger_sums_to_zero()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 120m);

        // 1. An order for part of what is on the shelf.
        var order = await CreateOrderAsync(sku, 80m);
        Assert.Equal("New", order.Status);

        // 2. Released: the rules choose the stock and the picks go into the queue.
        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        Assert.Equal("Released", released.Order.Status);
        Assert.Equal(1, released.AllocationsRaised);
        Assert.Equal(1, released.TasksRaised);
        Assert.Equal(0m, released.ShortfallQuantity);

        var allocation = released.Order.Lines.Single().Allocations.Single();
        Assert.Equal(80m, allocation.Quantity);
        Assert.Equal(bin, allocation.LocationCode);
        Assert.NotEqual(Guid.Empty, allocation.DecisionTraceId);
        Assert.NotNull(allocation.WorkTaskId);

        // The stock is held rather than moved: still on the shelf, no longer
        // promisable. This is the distinction Phase 2 deliberately did not draw.
        var held = await HoldingAsync(sku, bin);
        Assert.Equal(120m, held.Quantity);
        Assert.Equal(80m, held.ReservedQuantity);
        Assert.Equal(40m, held.AvailableQuantity);

        // 3. The pick is an ordinary job on the ordinary queue, and it explains
        //    itself the same way a putaway does.
        var claimed = await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/claim", new { }));

        Assert.Equal("Pick", claimed.Task.Kind);
        Assert.Equal(bin, claimed.Task.FromLocationCode);
        Assert.Equal(_bench, claimed.Task.SuggestedLocationCode);
        Assert.Equal(80m, claimed.Task.Quantity);

        Assert.NotNull(claimed.Decision);
        Assert.Equal("Standard allocation", claimed.Decision.RuleSetName);
        Assert.Equal("Allocation", claimed.Decision.Kind);

        var winner = claimed.Decision.Candidates.Single(c => c.Rank == 1);
        Assert.True(winner.IsAdmissible);
        Assert.NotEmpty(winner.Scores);
        Assert.All(winner.Scores, s => Assert.StartsWith("rules.allocation.", s.Reason.Code));

        // 4. Confirmed at the bench — a scan, exactly as a putaway is.
        var completed = await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{claimed.Task.Id}/complete", new { toLocationCode = _bench }));

        Assert.Equal("Completed", completed.Task.Status);
        Assert.False(completed.Task.WasOverridden);
        Assert.Single(completed.Movements);

        Assert.Equal(40m, await BalanceAsync(sku, bin));
        Assert.Equal(80m, await BalanceAsync(sku, _bench));

        // The claim was discharged by the movement that fulfilled it, not left
        // standing against stock that has already gone.
        var afterPick = await HoldingAsync(sku, bin);
        Assert.Equal(0m, afterPick.ReservedQuantity);
        Assert.Equal(40m, afterPick.AvailableQuantity);

        // 5. Packed, then despatched.
        var packed = await Read<OrderBody>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/pack", new { }));

        Assert.Equal("Packed", packed.Status);

        var despatched = await Read<DespatchResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/despatch", new { }));

        Assert.Equal("Despatched", despatched.Order.Status);
        Assert.Single(despatched.Movements);

        // 6. Four movements, and the sum over every holding — physical and
        //    virtual — is zero. Stock was never created and never destroyed; it
        //    came in from Supplier, moved about the building and left to
        //    Customer, which is the whole claim ADR 0001 makes.
        var movements = await MovementsAsync(sku);
        Assert.Equal(
            ["Receipt", "Putaway", "Pick", "Despatch"],
            movements.OrderBy(m => m.Sequence).Select(m => m.Type).ToArray());

        var pick = movements.Single(m => m.Type == "Pick");
        Assert.Equal(bin, pick.FromLocationCode);
        Assert.Equal(_bench, pick.ToLocationCode);
        Assert.Equal(claimed.Task.Id, pick.TaskId);

        // Directed work carries its reasoning out as well as in.
        Assert.Equal(allocation.DecisionTraceId, pick.DecisionTraceId);

        Assert.Equal(0m, await EveryHoldingAsync(sku));
        Assert.Equal(0m, await BalanceAsync(sku, _bench));
        Assert.Equal(40m, await BalanceAsync(sku, bin));
    }

    // ------------------------------------------------- available, not on hand

    [Fact]
    public async Task Stock_promised_to_one_order_cannot_be_promised_to_another()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 50m);

        var first = await CreateOrderAsync(sku, 50m);
        var second = await CreateOrderAsync(sku, 50m);

        var firstRelease = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{first.Id}/release", new { stagingLocationCode = _bench }));

        Assert.Equal(0m, firstRelease.ShortfallQuantity);

        var secondRelease = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{second.Id}/release", new { stagingLocationCode = _bench }));

        // Not an error. The stock is genuinely there and genuinely unavailable,
        // and a warehouse tells the difference between "we have none" and "it is
        // spoken for" because the two lead to entirely different phone calls.
        Assert.Equal(50m, secondRelease.ShortfallQuantity);
        Assert.Empty(secondRelease.Order.Lines.Single().Allocations);

        var trace = await GetAsync<TraceView>(
            $"/api/v1/rules/traces/{await LastAllocationTraceAsync(sku)}");

        var refused = trace.Candidates.Single(c => c.CandidateKey.StartsWith(bin, StringComparison.Ordinal));
        Assert.False(refused.IsAdmissible);
        Assert.NotNull(refused.ExcludedBy);
        Assert.Equal("rules.allocation.fully_committed", refused.ExcludedBy.Code);
        Assert.Equal("50", refused.ExcludedBy.Args["onHand"]);
        Assert.Equal("50", refused.ExcludedBy.Args["reserved"]);
    }

    [Fact]
    public async Task Two_orders_racing_for_the_last_pallet_do_not_both_get_it()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 30m);

        var orders = new[]
        {
            await CreateOrderAsync(sku, 30m),
            await CreateOrderAsync(sku, 30m)
        };

        // Released at the same instant, so both evaluations see the same free
        // stock. Only the reservation is serialised, and it is the only thing
        // that has to be.
        var releases = await Task.WhenAll(orders.Select(order => Read<ReleaseResult>(PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }))));

        Assert.Equal(1, releases.Count(r => r.AllocationsRaised == 1));
        Assert.Equal(1, releases.Count(r => r.ShortfallQuantity == 30m));

        // And the warehouse is committed to exactly one of them.
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&status=Available");

        Assert.Equal(30m, page.Items.Sum(r => r.ReservedQuantity ?? 0m));
    }

    // ------------------------------------------------------------- the rules

    [Fact]
    public async Task The_batch_that_expires_first_is_allocated_first_and_the_trace_says_why()
    {
        var sku = await NewItemAsync(lotTracked: true, expiryTracked: true);

        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(40));
        var later = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(300));

        // Deliberately received in the wrong order, so a first-in-first-out
        // implementation would pick the later batch and pass a weaker test.
        var youngBin = await ReceiveAndPutAwayAsync(sku, 60m, lot: "LOT-YOUNG", expiry: later);
        var oldBin = await ReceiveAndPutAwayAsync(sku, 60m, lot: "LOT-OLD", expiry: soon);

        var order = await CreateOrderAsync(sku, 60m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        var allocation = released.Order.Lines.Single().Allocations.Single();
        Assert.Equal(oldBin, allocation.LocationCode);
        Assert.Equal("LOT-OLD", allocation.LotNumber);
        Assert.NotEqual(youngBin, allocation.LocationCode);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{allocation.DecisionTraceId}");

        var chosen = trace.Candidates.Single(c => c.Rank == 1);
        var rotation = chosen.Scores.Single(s => s.RuleCode == "allocation.fefo");

        Assert.Equal("rules.allocation.expires_on", rotation.Reason.Code);
        Assert.Equal(soon.ToString("yyyy-MM-dd"), rotation.Reason.Args["expiry"]);

        // The younger batch was seen, ranked and beaten — not filtered out. A
        // supervisor asking "why not the fresh stock?" gets a number rather than
        // silence.
        var runnerUp = trace.Candidates.Single(c => c.Rank == 2);
        Assert.True(runnerUp.IsAdmissible);
        Assert.True(runnerUp.Score < chosen.Score);
    }

    [Fact]
    public async Task Short_dated_stock_is_refused_even_though_it_has_not_expired()
    {
        // 100 days of life, and the customer will not take anything with less than
        // half of it left.
        var sku = await NewItemAsync(
            lotTracked: true, expiryTracked: true, shelfLifeDays: 100, minimumRemainingPercent: 50);

        var shortDated = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));
        var fresh = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90));

        var shortBin = await ReceiveAndPutAwayAsync(sku, 40m, lot: "LOT-SHORT", expiry: shortDated);
        var freshBin = await ReceiveAndPutAwayAsync(sku, 40m, lot: "LOT-FRESH", expiry: fresh);

        var order = await CreateOrderAsync(sku, 40m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        var allocation = released.Order.Lines.Single().Allocations.Single();

        // FEFO would have taken the short-dated batch. The constraint outranks the
        // preference, which is the whole difference between the two — and the
        // reason Item.MinimumRemainingShelfLifePercent exists.
        Assert.Equal(freshBin, allocation.LocationCode);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{allocation.DecisionTraceId}");

        var refused = trace.Candidates.Single(
            c => !c.IsAdmissible && c.CandidateKey.StartsWith(shortBin, StringComparison.Ordinal));

        Assert.Equal("rules.allocation.short_dated", refused.ExcludedBy!.Code);
        Assert.Equal("50", refused.ExcludedBy.Args["required"]);
        Assert.Equal("20", refused.ExcludedBy.Args["remaining"]);
    }

    [Fact]
    public async Task Quarantined_stock_is_considered_and_named_rather_than_quietly_skipped()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 25m, condition: "Quarantine");
        await ReceiveAndPutAwayAsync(sku, 25m);

        var order = await CreateOrderAsync(sku, 25m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        var allocation = released.Order.Lines.Single().Allocations.Single();
        Assert.Equal(25m, allocation.Quantity);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{allocation.DecisionTraceId}");

        // The point is that it appears at all. Filtering held stock out of the
        // query would have been cheaper and would have made "there are twenty-five
        // in quarantine" — very often the answer the person chasing a short order
        // actually needs — unobtainable.
        //
        // Consolidation quite reasonably puts both pallets in the same bay, so the
        // two holdings share a location and are told apart by their condition —
        // which is exactly the case the candidate key carries it for.
        var held = trace.Candidates.Single(c => c.CandidateKey.EndsWith("(Quarantine)", StringComparison.Ordinal));

        Assert.False(held.IsAdmissible);
        Assert.Equal("rules.allocation.not_allocatable", held.ExcludedBy!.Code);
        Assert.Equal("Quarantine", held.ExcludedBy.Args["condition"]);

        var chosen = trace.Candidates.Single(c => c.Rank == 1);
        Assert.DoesNotContain("(Quarantine)", chosen.CandidateKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stock_still_standing_on_the_dock_is_not_allocatable()
    {
        var sku = await NewItemAsync();

        // Received and left where it landed — no putaway, so nobody has confirmed
        // where it is and a picker sent to a dock door would be walking into a
        // truck.
        await Read<DockReceipt>(await PostAsync(
            _operator, "/api/v1/inbound/receipts", new { sku, quantity = 90m, toLocationCode = _dock }));

        var order = await CreateOrderAsync(sku, 90m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        Assert.Equal(90m, released.ShortfallQuantity);

        var trace = await GetAsync<TraceView>($"/api/v1/rules/traces/{await LastAllocationTraceAsync(sku)}");
        var refused = trace.Candidates.Single(c => c.CandidateKey.StartsWith(_dock, StringComparison.Ordinal));

        Assert.Equal("rules.allocation.not_pickable", refused.ExcludedBy!.Code);
        Assert.Equal("Dock", refused.ExcludedBy.Args["role"]);
    }

    [Fact]
    public async Task A_line_is_filled_from_several_holdings_when_no_single_one_covers_it()
    {
        // Two batches, because putaway would otherwise consolidate two pallets of
        // one SKU into one bay — quite correctly — and there would be a single
        // holding to draw on. Lot purity keeps them apart, which is what a
        // warehouse with batch-tracked goods actually looks like.
        var sku = await NewItemAsync(lotTracked: true, expiryTracked: true);
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(120));

        await ReceiveAndPutAwayAsync(sku, 30m, lot: "LOT-A", expiry: expiry);
        await ReceiveAndPutAwayAsync(sku, 30m, lot: "LOT-B", expiry: expiry);

        var order = await CreateOrderAsync(sku, 45m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        // One decision, two lines, two jobs. The rules rank; the fill walks the
        // ranking taking what each holding has.
        Assert.Equal(2, released.AllocationsRaised);
        Assert.Equal(2, released.TasksRaised);
        Assert.Equal(0m, released.ShortfallQuantity);

        var allocations = released.Order.Lines.Single().Allocations;
        Assert.Equal(45m, allocations.Sum(a => a.Quantity));
        Assert.Equal(["LOT-A", "LOT-B"], allocations.Select(a => a.LotNumber ?? string.Empty).Order().ToArray());

        // The second holding gives up only what is still outstanding, rather than
        // everything it has.
        Assert.Equal([15m, 30m], allocations.Select(a => a.Quantity).Order().ToArray());

        // Both came out of the same evaluation, so "why these two?" has one answer
        // rather than two half-answers.
        Assert.Single(allocations.Select(a => a.DecisionTraceId).Distinct());
    }

    // --------------------------------------------------------- exactly once

    [Fact]
    public async Task A_retried_pick_confirmation_posts_once()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 20m);
        var (taskId, _) = await ReleaseAndClaimAsync(sku, 20m);

        var key = Guid.CreateVersion7().ToString();

        var first = await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench }, key));

        var replay = await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench }, key));

        Assert.Equal("Completed", first.Task.Status);
        Assert.Equal("Completed", replay.Task.Status);

        Assert.Single(await MovementsOfTypeAsync(sku, "Pick"));
        Assert.Equal(0m, await BalanceAsync(sku, bin));
        Assert.Equal(20m, await BalanceAsync(sku, _bench));
    }

    [Fact]
    public async Task A_second_confirmation_of_a_picked_job_is_refused()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 20m);
        var (taskId, _) = await ReleaseAndClaimAsync(sku, 20m);

        await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench }));

        // A new key is a new intent, and honouring it would take the stock a
        // second time — against a commitment that no longer exists.
        var second = await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(await MovementsOfTypeAsync(sku, "Pick"));
    }

    [Fact]
    public async Task A_retried_despatch_posts_once()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 15m);
        var (taskId, orderId) = await ReleaseAndClaimAsync(sku, 15m);

        await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench }));

        await PostAsync(_supervisor, $"/api/v1/outbound/orders/{orderId}/pack", new { });

        var key = Guid.CreateVersion7().ToString();

        var first = await Read<DespatchResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{orderId}/despatch", new { }, key));

        var replay = await Read<DespatchResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{orderId}/despatch", new { }, key));

        Assert.Single(first.Movements);
        Assert.Empty(replay.Movements);
        Assert.Equal("Despatched", replay.Order.Status);

        Assert.Single(await MovementsOfTypeAsync(sku, "Despatch"));
        Assert.Equal(0m, await EveryHoldingAsync(sku));

        // A second despatch under a different key is a second trailer, and is
        // refused rather than posted.
        var again = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{orderId}/despatch", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_release_repeated_under_the_same_key_commits_the_stock_once()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 60m);

        var order = await CreateOrderAsync(sku, 25m);
        var key = Guid.CreateVersion7().ToString();

        var first = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }, key));

        var again = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }, key));

        Assert.Equal(1, first.AllocationsRaised);
        Assert.Equal(0, again.AllocationsRaised);
        Assert.Equal(0, again.TasksRaised);

        Assert.Single(again.Order.Lines.Single().Allocations);
        Assert.Equal(25m, again.Order.Lines.Single().AllocatedQuantity);

        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&status=Available");

        Assert.Equal(25m, page.Items.Sum(r => r.ReservedQuantity ?? 0m));
    }

    // ------------------------------------------------------------ the guards

    [Fact]
    public async Task An_order_cannot_be_packed_while_a_pick_is_still_open()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 12m);
        var (_, orderId) = await ReleaseAndClaimAsync(sku, 12m);

        var response = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{orderId}/pack", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_order_cannot_be_despatched_before_it_is_packed()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 12m);
        var (taskId, orderId) = await ReleaseAndClaimAsync(sku, 12m);

        await Read<TaskCompletion>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{taskId}/complete", new { toLocationCode = _bench }));

        var response = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{orderId}/despatch", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await MovementsOfTypeAsync(sku, "Despatch"));
    }

    [Fact]
    public async Task Cancelling_an_order_gives_the_stock_back_and_withdraws_the_picks()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 40m);

        var order = await CreateOrderAsync(sku, 40m);

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { stagingLocationCode = _bench }));

        var taskId = released.Order.Lines.Single().Allocations.Single().WorkTaskId;
        Assert.Equal(40m, (await HoldingAsync(sku, bin)).ReservedQuantity);

        var cancelled = await Read<CancelResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/cancel", new { reason = "Customer withdrew" }));

        Assert.Equal("Cancelled", cancelled.Order.Status);
        Assert.Equal(1, cancelled.ReservationsReleased);
        Assert.Equal(1, cancelled.TasksCancelled);

        // The stock never moved, so nothing needs unwinding in the ledger — which
        // is exactly why a reservation is not a movement.
        var afterwards = await HoldingAsync(sku, bin);
        Assert.Equal(40m, afterwards.Quantity);
        Assert.Equal(0m, afterwards.ReservedQuantity);
        Assert.Equal(40m, afterwards.AvailableQuantity);

        Assert.Empty(await MovementsOfTypeAsync(sku, "Pick"));

        var task = await GetAsync<TaskDetail>($"/api/v1/work/tasks/{taskId}");
        Assert.Equal("Cancelled", task.Task.Status);
    }

    [Fact]
    public async Task An_order_whose_pick_somebody_is_holding_cannot_be_cancelled_underneath_them()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 18m);
        var (_, orderId) = await ReleaseAndClaimAsync(sku, 18m);

        var response = await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{orderId}/cancel", new { reason = "Changed their mind" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_order_cannot_be_released_without_somewhere_to_drop_the_picks()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 10m);

        var order = await CreateOrderAsync(sku, 10m, staging: null);

        var response = await PostAsync(_supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Racking_is_not_somewhere_an_order_can_be_staged()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 10m);

        // Dropping a pick back into a pick face would put the stock straight back
        // where the next allocation would find it.
        var response = await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewOrderReference(),
                stagingLocationCode = bin,
                lines = new[] { new { sku, quantity = 10m } }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------- the preview

    [Fact]
    public async Task A_preview_answers_the_same_question_and_commits_nothing()
    {
        var sku = await NewItemAsync();
        var bin = await ReceiveAndPutAwayAsync(sku, 70m);

        var preview = await Read<PreviewResult>(await PostAsync(
            _supervisor,
            "/api/v1/rules/allocation/preview",
            new { warehouseCode = _warehouseCode, sku, quantity = 70m }));

        Assert.Equal(bin, preview.Lines.Single().LocationCode);
        Assert.Equal(70m, preview.AllocatedQuantity);
        Assert.Equal(0m, preview.ShortfallQuantity);
        Assert.NotEmpty(preview.Trace.Candidates);

        // Asking the question does not answer it differently next time: nothing is
        // held, so the stock a supervisor was told about is still promisable.
        var holding = await HoldingAsync(sku, bin);
        Assert.Equal(0m, holding.ReservedQuantity);
        Assert.Equal(70m, holding.AvailableQuantity);
    }

    // ------------------------------------------------------------- the wave

    [Fact]
    public async Task A_wave_releases_every_order_in_it_through_the_same_path()
    {
        var sku = await NewItemAsync();
        await ReceiveAndPutAwayAsync(sku, 100m);

        var first = await CreateOrderAsync(sku, 20m);
        var second = await CreateOrderAsync(sku, 30m);

        var wave = await Read<WaveBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/waves",
            new
            {
                warehouseCode = _warehouseCode,
                reference = $"WAVE-{Interlocked.Increment(ref _counter):D4}-{Guid.CreateVersion7():N}"[..24],
                orderIds = new[] { first.Id, second.Id }
            }));

        var released = await Read<ReleaseWaveResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/waves/{wave.Id}/release", new { stagingLocationCode = _bench }));

        Assert.Equal("Released", released.Wave.Status);
        Assert.Equal(2, released.OrdersReleased);
        Assert.Equal(2, released.TasksRaised);
        Assert.All(released.Wave.Orders, order => Assert.Equal("Released", order.Status));

        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&status=Available");

        Assert.Equal(50m, page.Items.Sum(r => r.ReservedQuantity ?? 0m));
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Puts stock on a shelf the way the warehouse actually does — a dock receipt,
    /// a rule-chosen destination and a confirmed putaway. Setting balances
    /// directly would be quicker and would test an arrangement of stock the system
    /// cannot reach.
    /// </summary>
    private async Task<string> ReceiveAndPutAwayAsync(
        string sku,
        decimal quantity,
        string? lot = null,
        DateOnly? expiry = null,
        string condition = "Available")
    {
        var received = await Read<DockReceipt>(await PostAsync(
            _operator,
            "/api/v1/inbound/receipts",
            new
            {
                sku,
                quantity,
                toLocationCode = _dock,
                lotNumber = lot,
                expiryDate = expiry,
                condition
            }));

        Assert.NotNull(received.SuggestedLocationCode);
        Assert.NotNull(received.WorkTaskId);

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = received.SuggestedLocationCode }));

        return received.SuggestedLocationCode!;
    }

    private async Task<OrderBody> CreateOrderAsync(
        string sku,
        decimal quantity,
        string? staging = "",
        [CallerMemberName] string caller = "")
    {
        return await Read<OrderBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewOrderReference(caller),
                customerName = "Test Customer BV",
                stagingLocationCode = staging == "" ? _bench : staging,
                lines = new[] { new { sku, quantity } }
            }));
    }

    /// <summary>Releases a one-line order and claims the single pick it raises.</summary>
    private async Task<(Guid TaskId, Guid OrderId)> ReleaseAndClaimAsync(
        string sku,
        decimal quantity,
        [CallerMemberName] string caller = "")
    {
        var order = await Read<OrderBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = NewOrderReference(caller),
                stagingLocationCode = _bench,
                lines = new[] { new { sku, quantity } }
            }));

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { }));

        var allocation = released.Order.Lines.Single().Allocations.Single();

        await Read<TaskDetail>(await PostAsync(
            _operator, $"/api/v1/work/tasks/{allocation.WorkTaskId}/claim", new { }));

        return (allocation.WorkTaskId!.Value, order.Id);
    }

    /// <summary>
    /// A reference no other test in this class will produce. The counter alone is
    /// not enough: xUnit builds a fresh instance per test method, so it restarts at
    /// zero every time while the database does not.
    /// </summary>
    private string NewOrderReference([CallerMemberName] string caller = "") =>
        $"SO-{caller[..Math.Min(caller.Length, 30)]}-{Interlocked.Increment(ref _counter):D2}";

    private async Task<string> NewItemAsync(
        bool lotTracked = false,
        bool expiryTracked = false,
        int? shelfLifeDays = null,
        int? minimumRemainingPercent = null,
        [CallerMemberName] string caller = "")
    {
        var sku = $"OUT-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Outbound test item for {caller}",
            BaseUom = "EA",
            AbcClass = AbcClass.B,
            IsLotTracked = lotTracked,
            IsExpiryTracked = expiryTracked,
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

    private async Task<BalanceRow> HoldingAsync(string sku, string locationCode)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}");

        return page.Items.Single();
    }

    private async Task<decimal> BalanceAsync(string sku, string locationCode)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status=Available");

        return page.Items.Sum(r => r.Quantity);
    }

    /// <summary>
    /// Every holding of the SKU anywhere, physical and virtual, summed. Zero is
    /// the double-entry claim: stock was neither created nor destroyed.
    /// </summary>
    private async Task<decimal> EveryHoldingAsync(string sku)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/v1/inventory/balances?sku={Uri.EscapeDataString(sku)}&includeEmpty=true&pageSize=500");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<List<MovementRow>> MovementsAsync(string sku) =>
        (await GetAsync<MovementPage>($"/api/v1/inventory/movements?sku={Uri.EscapeDataString(sku)}")).Items;

    private async Task<List<MovementRow>> MovementsOfTypeAsync(string sku, string type) =>
        [.. (await MovementsAsync(sku)).Where(m => m.Type == type)];

    /// <summary>
    /// The trace behind the most recent allocation for a SKU, for the cases where
    /// nothing was allocated and so no allocation row carries it.
    /// </summary>
    private async Task<Guid> LastAllocationTraceAsync(string sku)
    {
        using var scope = fixture.Services.CreateScope();
        var rules = scope.ServiceProvider
            .GetRequiredService<Countflow.Modules.Rules.Persistence.RulesDbContext>();

        // Matched in memory rather than in the query: InputsJson is a jsonb column
        // and Postgres has no LIKE for jsonb, so a server-side Contains fails with
        // "operator does not exist: jsonb ~~ jsonb".
        var recent = await rules.DecisionTraces
            .AsNoTracking()
            .Where(t => t.Kind == Countflow.SharedKernel.Rules.DecisionKind.Allocation)
            .OrderByDescending(t => t.DecidedAt)
            .Take(200)
            .Select(t => new { t.Id, t.InputsJson })
            .ToListAsync();

        return recent.First(t => t.InputsJson.Contains(sku, StringComparison.Ordinal)).Id;
    }

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

    private static async Task<T> Read<T>(Task<HttpResponseMessage> pending) => await Read<T>(await pending);

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

        await EnsureAsync("outbound-supervisor", "OUT-SUP", Roles.Supervisor);
        await EnsureAsync("outbound-operator", "OUT-OP1", Roles.Operator);
        await EnsureAsync("outbound-operator-2", "OUT-OP2", Roles.Operator);

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

    private sealed record PostedMovementBody(Guid MovementId, long Sequence, decimal Quantity, bool WasAlreadyPosted);

    private sealed record DockReceipt(
        string Sku,
        string? SuggestedLocationCode,
        Guid? WorkTaskId,
        Guid DecisionTraceId);

    private sealed record AllocationBody(
        Guid Id,
        decimal Quantity,
        string LocationCode,
        string? LotNumber,
        DateOnly? ExpiryDate,
        Guid DecisionTraceId,
        Guid? WorkTaskId,
        string? TaskState,
        decimal PickedQuantity,
        string? PickedAtLocationCode);

    private sealed record OrderLineBody(
        Guid Id,
        int LineNumber,
        string Sku,
        decimal OrderedQuantity,
        decimal AllocatedQuantity,
        decimal OutstandingQuantity,
        decimal PickedQuantity,
        List<AllocationBody> Allocations);

    private sealed record OrderBody(
        Guid Id,
        string Reference,
        string Status,
        string? StagingLocationCode,
        List<OrderLineBody> Lines);

    private sealed record ReleaseResult(
        OrderBody Order,
        int AllocationsRaised,
        int TasksRaised,
        decimal ShortfallQuantity);

    private sealed record DespatchResult(OrderBody Order, List<PostedMovementBody> Movements);

    private sealed record CancelResult(OrderBody Order, int ReservationsReleased, int TasksCancelled);

    private sealed record WaveBody(Guid Id, string Reference, string Status, List<OrderBody> Orders);

    private sealed record ReleaseWaveResult(
        WaveBody Wave,
        int OrdersReleased,
        int TasksRaised,
        decimal ShortfallQuantity);

    private sealed record PreviewLine(string LocationCode, string? LotNumber, decimal Quantity);

    private sealed record PreviewResult(
        Guid DecisionTraceId,
        List<PreviewLine> Lines,
        decimal AllocatedQuantity,
        decimal ShortfallQuantity,
        TraceView Trace);

    private sealed record TaskBody(
        Guid Id,
        string Kind,
        string Status,
        string Sku,
        decimal Quantity,
        string FromLocationCode,
        string? SuggestedLocationCode,
        Guid? DecisionTraceId,
        string? CompletedAtLocationCode,
        bool WasOverridden);

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

    private sealed record BalanceRow(
        string LocationCode,
        decimal Quantity,
        decimal? ReservedQuantity,
        decimal? AvailableQuantity);

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
