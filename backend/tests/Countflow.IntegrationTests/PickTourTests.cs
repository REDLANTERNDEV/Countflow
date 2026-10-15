using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Routing;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Sequencing a pick list, end to end.
/// <para>
/// The first test is the phase's definition of done: picks raised in the order a
/// customer typed their order lines come back in the order somebody would walk them.
/// The rest defend the properties that make that worth anything — that two pickers on
/// one queue get disjoint tours rather than interleaved halves of one, that an
/// abandoned tour can be handed back in one move, and that a zone is refused a
/// strategy its bins cannot support at the moment somebody chooses it rather than at
/// a rack.
/// </para>
/// </summary>
public sealed class PickTourTests(CountflowApiFixture fixture)
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

        _supervisor = await SignInAsync("tour-supervisor");
        _operator = await SignInAsync("tour-operator");
        _secondOperator = await SignInAsync("tour-operator-2");

        // xUnit runs the methods of one class in sequence against one database, so
        // picks left standing by an earlier test would be swept into a later test's
        // tour. Draining is the honest reset: a tour is claimed from the whole queue
        // by design, and pretending otherwise here would test a queue this system
        // does not have.
        await DrainThePickQueueAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------- the walk

    /// <summary>
    /// The phase's definition of done.
    /// <para>
    /// Four lines, deliberately entered scrambled, with an empty aisle in the middle
    /// of them. Ambient is seeded as an S-Shape hall with a cross-aisle at each end,
    /// so the walk is A01 outward and then — because A02 holds nothing and the picker
    /// is still at the far end — A03 back to front. <b>That last aisle is the whole
    /// argument</b>: ordering by the bins' own pick sequence would send them to A03's
    /// first bay, a full aisle-length wrong, and no sort of any per-bin key can get it
    /// right because the answer depends on whether A02 was in the set.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Picks_raised_in_order_line_order_come_back_in_the_order_somebody_walks_them()
    {
        var shallowFirstAisle = await StockAsync("A01", "03");
        var deepFirstAisle = await StockAsync("A01", "08");
        var shallowThirdAisle = await StockAsync("A03", "02");
        var deepThirdAisle = await StockAsync("A03", "07");

        // The order a customer typed them, which is the order the picks are raised
        // in and the order the queue would otherwise hand them out in.
        await ReleaseOrderAsync(
            shallowThirdAisle.Sku, deepFirstAisle.Sku, deepThirdAisle.Sku, shallowFirstAisle.Sku);

        var tour = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 12 }));

        Assert.Equal(4, tour.StopCount);
        Assert.Equal(4, tour.Remaining);
        Assert.False(tour.IsComplete);

        Assert.Equal(
            [
                shallowFirstAisle.Bin,
                deepFirstAisle.Bin,
                deepThirdAisle.Bin,
                shallowThirdAisle.Bin
            ],
            tour.Stops.Select(s => s.FromLocationCode).ToArray());

        // The sequence is on the stops as well as in their order, so a handheld out
        // of signal can put what it is holding in order without a second fetch.
        Assert.Equal([1, 2, 3, 4], tour.Stops.Select(s => s.TourSequence).ToArray());
        Assert.All(tour.Stops, stop => Assert.Equal(tour.Id, stop.PickTourId));
        Assert.All(tour.Stops, stop => Assert.Equal("Assigned", stop.Status));

        // What produced the walk, stamped, because the zone's setting is a plain
        // column with no promotion lifecycle behind it and will have moved on.
        var segment = Assert.Single(tour.Segments);
        Assert.Equal("AMB", segment.ZoneCode);
        Assert.Equal(PickRouteCatalogue.SShape, segment.RequestedStrategy);
        Assert.Equal(PickRouteCatalogue.SShape, segment.AppliedStrategy);
        Assert.Null(segment.FellBackBecause);

        // And the instrument: measured against working the same stops in the order
        // they were raised.
        Assert.NotNull(tour.TravelSteps);
        Assert.NotNull(tour.BaselineTravelSteps);
        Assert.NotNull(tour.StepsSaved);
        Assert.True(
            tour.StepsSaved > 0,
            $"expected the walk to beat order-line order, got {tour.TravelSteps} against {tour.BaselineTravelSteps}");
    }

    /// <summary>
    /// A tour read back is the same tour. The handheld fetches it again after a
    /// reload, and the console watches one somebody else is on.
    /// </summary>
    [Fact]
    public async Task A_claimed_tour_can_be_read_back_with_its_stops_in_order()
    {
        await StockAsync("A05", "02");
        await StockAsync("A05", "09");
        var skus = await StockedSkusAsync("A07", "04");

        await ReleaseOrderAsync([.. skus]);

        var claimed = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 12 }));

        var reread = await GetAsync<TourBody>($"/api/v1/work/tours/{claimed.Id}");

        Assert.Equal(claimed.StopCount, reread.StopCount);
        Assert.Equal(
            claimed.Stops.Select(s => s.Id).ToArray(),
            reread.Stops.Select(s => s.Id).ToArray());
        Assert.Equal(claimed.TravelSteps, reread.TravelSteps);
    }

    // --------------------------------------------------- two on one queue

    [Fact]
    public async Task Retrying_a_claim_with_the_same_key_returns_the_same_tour()
    {
        var firstSku = await StockAsync("A02", "01");
        var secondSku = await StockAsync("A02", "05");
        await ReleaseOrderAsync(firstSku.Sku, secondSku.Sku);
        var key = Guid.CreateVersion7().ToString();

        async Task<TourBody> Claim()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/work/tours/claim-next")
            {
                Content = JsonContent.Create(new { maxStops = 1 })
            };
            request.Headers.Add("Idempotency-Key", key);
            return await Read<TourBody>(await _operator.SendAsync(request));
        }

        var first = await Claim();
        var retry = await Claim();
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.Stops.Select(s => s.Id), retry.Stops.Select(s => s.Id));
    }

    [Fact]
    public async Task Concurrent_claims_with_one_key_create_only_one_tour()
    {
        var sku = await StockAsync("A03", "02");
        await ReleaseOrderAsync(sku.Sku);
        var key = Guid.CreateVersion7().ToString();
        async Task<TourBody> Claim()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/work/tours/claim-next")
            {
                Content = JsonContent.Create(new { maxStops = 1 })
            };
            request.Headers.Add("Idempotency-Key", key);
            return await Read<TourBody>(await _operator.SendAsync(request));
        }
        var results = await Task.WhenAll(Claim(), Claim());
        Assert.Equal(results[0].Id, results[1].Id);
    }

    [Fact]
    public async Task A_tour_claim_requires_an_idempotency_key()
    {
        var response = await _operator.PostAsJsonAsync("/api/v1/work/tours/claim-next", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_claim_with_a_warehouse_filter_never_takes_another_warehouses_work()
    {
        var sku = await StockAsync("A03", "01");
        await ReleaseOrderAsync(sku.Sku);
        var response = await PostAsync(_operator, "/api/v1/work/tours/claim-next",
            new { warehouseId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var tour = await Read<TourBody>(await PostAsync(_operator,
            "/api/v1/work/tours/claim-next", new { maxStops = 0 }));
        Assert.Equal(1, tour.StopCount);
    }

    [Fact]
    public async Task Concurrent_release_retries_return_success_without_releasing_twice()
    {
        var sku = await StockAsync("A04", "02");
        await ReleaseOrderAsync(sku.Sku);
        var tour = await Read<TourBody>(await PostAsync(_operator,
            "/api/v1/work/tours/claim-next", new { }));
        var key = Guid.CreateVersion7().ToString();
        async Task<HttpResponseMessage> Release()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/work/tours/{tour.Id}/release");
            request.Headers.Add("Idempotency-Key", key);
            return await _operator.SendAsync(request);
        }
        var results = await Task.WhenAll(Release(), Release());
        Assert.All(results, response => Assert.True(response.IsSuccessStatusCode));
        var after = await GetAsync<TourBody>($"/api/v1/work/tours/{tour.Id}");
        Assert.Equal(1, after.ReleasedStops);
    }

    [Fact]
    public async Task Completing_and_releasing_one_stop_concurrently_preserves_the_completion()
    {
        var sku = await StockAsync("A05", "02");
        await ReleaseOrderAsync(sku.Sku);
        var tour = await Read<TourBody>(await PostAsync(_operator,
            "/api/v1/work/tours/claim-next", new { }));
        var results = await Task.WhenAll(
            PostAsync(_operator, $"/api/v1/work/tasks/{tour.Stops[0].Id}/complete", new { toLocationCode = _bench }),
            PostAsync(_operator, $"/api/v1/work/tours/{tour.Id}/release", new { }));
        Assert.True(results[0].IsSuccessStatusCode, await results[0].Content.ReadAsStringAsync());
        Assert.True(results[1].IsSuccessStatusCode || results[1].StatusCode == HttpStatusCode.Conflict);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var task = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == tour.Stops[0].Id);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task A_delayed_completion_from_a_released_tour_is_refused()
    {
        var sku = await StockAsync("A05", "04");
        await ReleaseOrderAsync(sku.Sku);
        var tour = await Read<TourBody>(await PostAsync(_operator,
            "/api/v1/work/tours/claim-next", new { }));
        Assert.True((await PostAsync(_operator, $"/api/v1/work/tours/{tour.Id}/release", new { })).IsSuccessStatusCode);
        var late = await PostAsync(_operator, $"/api/v1/work/tasks/{tour.Stops[0].Id}/complete",
            new { toLocationCode = _bench, pickTourId = tour.Id });
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
    }

    /// <summary>
    /// The tension the design exists to resolve.
    /// <para>
    /// Tasks are handed out one at a time from a shared queue under
    /// <c>SKIP LOCKED</c>, so two pickers on one wave interleave and the walk
    /// dissolves. Claiming a <i>set</i> is what stops that — and because the tour is
    /// built from what was actually locked rather than sequenced in advance and
    /// carved up, neither picker ends up walking a subsequence of somebody else's
    /// plan.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_operators_on_one_queue_get_disjoint_tours_rather_than_halves_of_one()
    {
        var skus = new List<string>();

        foreach (var (aisle, bay) in new[]
                 {
                     ("A02", "01"), ("A02", "06"), ("A04", "03"),
                     ("A04", "08"), ("A06", "02"), ("A06", "07")
                 })
        {
            skus.Add((await StockAsync(aisle, bay)).Sku);
        }

        await ReleaseOrderAsync([.. skus]);

        var first = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 3 }));

        var second = await Read<TourBody>(await PostAsync(
            _secondOperator, "/api/v1/work/tours/claim-next", new { maxStops = 3 }));

        Assert.Equal(3, first.StopCount);
        Assert.Equal(3, second.StopCount);
        Assert.NotEqual(first.Id, second.Id);

        var mine = first.Stops.Select(s => s.Id).ToHashSet();
        var theirs = second.Stops.Select(s => s.Id).ToHashSet();

        Assert.Empty(mine.Intersect(theirs));

        // Each tour is sequenced over its own membership, so both are complete walks
        // rather than a plan with holes in it.
        Assert.Equal([1, 2, 3], first.Stops.Select(s => s.TourSequence).ToArray());
        Assert.Equal([1, 2, 3], second.Stops.Select(s => s.TourSequence).ToArray());
    }

    [Fact]
    public async Task An_empty_queue_is_no_work_available_rather_than_an_empty_tour()
    {
        var response = await PostAsync(_operator, "/api/v1/work/tours/claim-next", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------ handing it back

    /// <summary>
    /// The cost of claiming a set, paid. Releasing twelve stops one at a time on a
    /// scanner is how they end up not released at all.
    /// </summary>
    [Fact]
    public async Task Releasing_a_tour_hands_back_what_was_not_walked_and_keeps_what_was()
    {
        var skus = new List<string>();

        foreach (var (aisle, bay) in new[] { ("A08", "01"), ("A08", "05"), ("A10", "02"), ("A10", "06") })
        {
            skus.Add((await StockAsync(aisle, bay)).Sku);
        }

        await ReleaseOrderAsync([.. skus]);

        var tour = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 12 }));

        Assert.Equal(4, tour.StopCount);

        // Two stops walked, then the operator goes home.
        foreach (var stop in tour.Stops.Take(2))
        {
            await Read<TaskCompletion>(await PostAsync(
                _operator, $"/api/v1/work/tasks/{stop.Id}/complete", new { toLocationCode = _bench }));
        }

        var released = await PostAsync(_operator, $"/api/v1/work/tours/{tour.Id}/release", new { });
        Assert.True(released.IsSuccessStatusCode);

        var after = await GetAsync<TourBody>($"/api/v1/work/tours/{tour.Id}");

        // The tour keeps the walk that happened and records what left it. StopCount
        // is what was claimed, and counting the survivors could not tell you it was
        // abandoned.
        Assert.Equal(4, after.StopCount);
        Assert.Equal(2, after.ReleasedStops);
        Assert.NotNull(after.ReleasedAt);
        Assert.Equal(2, after.Stops.Count);
        Assert.All(after.Stops, stop => Assert.Equal("Completed", stop.Status));
        Assert.Equal(0, after.Remaining);
        Assert.True(after.IsComplete);

        // And the two that were handed back are ordinary queued work again, on no
        // walk at all.
        var queued = await GetAsync<TaskPage>("/api/v1/work/tasks?status=Ready&kind=Pick&pageSize=200");
        var handedBack = queued.Items.Where(t => tour.Stops.Skip(2).Any(s => s.Id == t.Id)).ToList();

        Assert.Equal(2, handedBack.Count);
        Assert.All(handedBack, task => Assert.Null(task.PickTourId));
        Assert.All(handedBack, task => Assert.Null(task.TourSequence));
    }

    [Fact]
    public async Task A_tour_belongs_to_the_operator_who_claimed_it()
    {
        await StockAsync("A12", "01");
        var skus = await StockedSkusAsync("A12", "04");

        await ReleaseOrderAsync([.. skus]);

        var tour = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 12 }));

        var refused = await PostAsync(_secondOperator, $"/api/v1/work/tours/{tour.Id}/release", new { });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_tour_with_nothing_left_on_it_cannot_be_released_twice()
    {
        var stocked = await StockAsync("A14", "02");
        await ReleaseOrderAsync(stocked.Sku);

        var tour = await Read<TourBody>(await PostAsync(
            _operator, "/api/v1/work/tours/claim-next", new { maxStops = 12 }));

        Assert.True((await PostAsync(_operator, $"/api/v1/work/tours/{tour.Id}/release", new { }))
            .IsSuccessStatusCode);

        var second = await PostAsync(_operator, $"/api/v1/work/tours/{tour.Id}/release", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // -------------------------------------------------- configuring a zone

    /// <summary>
    /// The third time this codebase validates a setting by running it through the
    /// real thing at the moment somebody presses save — after a rule set built
    /// through its catalogue and a label template rendered against a specimen.
    /// </summary>
    [Fact]
    public async Task A_zone_is_refused_a_strategy_its_geometry_cannot_support()
    {
        var quarantine = await ZoneAsync("QUAR");

        var refused = await PutAsync(
            _supervisor,
            $"/api/v1/zones/{quarantine.Id}/pick-route",
            new { strategy = PickRouteCatalogue.SShape, hasRearCrossAisle = false });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("masterdata.route_strategy_unsupported", problem!.Code);
        Assert.Contains("route.no_rear_cross_aisle", problem.Detail);

        // Refused means unchanged, not partially applied.
        Assert.Equal(PickRouteCatalogue.SequenceOrdinal, (await ZoneAsync("QUAR")).PickRouteStrategy);
    }

    [Fact]
    public async Task A_strategy_and_the_cross_aisle_it_needs_are_set_in_one_move()
    {
        var quarantine = await ZoneAsync("QUAR");

        var accepted = await Read<ZoneBody>(await PutAsync(
            _supervisor,
            $"/api/v1/zones/{quarantine.Id}/pick-route",
            new { strategy = PickRouteCatalogue.Midpoint, hasRearCrossAisle = true }));

        Assert.Equal(PickRouteCatalogue.Midpoint, accepted.PickRouteStrategy);
        Assert.True(accepted.HasRearCrossAisle);

        // One aisle of ten bays, which is what makes every geometric option open to
        // it once it has declared a cross-aisle.
        Assert.Equal(1, accepted.Aisles);
        Assert.Equal(10, accepted.Bays);
        Assert.All(accepted.RouteOptions, option => Assert.True(option.IsSupported));

        // Put back, so the zone this class leaves behind is the one it found.
        await PutAsync(
            _supervisor,
            $"/api/v1/zones/{quarantine.Id}/pick-route",
            new { strategy = PickRouteCatalogue.SequenceOrdinal, hasRearCrossAisle = false });
    }

    [Fact]
    public async Task A_strategy_this_build_does_not_have_is_refused_rather_than_defaulted()
    {
        var quarantine = await ZoneAsync("QUAR");

        var refused = await PutAsync(
            _supervisor,
            $"/api/v1/zones/{quarantine.Id}/pick-route",
            new { strategy = "route.invented_last_tuesday", hasRearCrossAisle = true });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("masterdata.unknown_route_strategy", problem!.Code);
    }

    /// <summary>
    /// The listing carries the reason an option is closed, not merely the fact. A
    /// console that greys out three of four choices teaches a supervisor the feature
    /// is broken; a sentence tells them what to go and enter.
    /// </summary>
    [Fact]
    public async Task The_zone_listing_says_why_an_option_is_not_open()
    {
        var chilled = (await GetAsync<List<ZoneBody>>("/api/v1/zones")).Single(z => z.Code == "CHL");

        Assert.Equal(PickRouteCatalogue.Return, chilled.PickRouteStrategy);
        Assert.False(chilled.HasRearCrossAisle);

        var sShape = chilled.RouteOptions.Single(o => o.Code == PickRouteCatalogue.SShape);

        Assert.False(sShape.IsSupported);
        Assert.Contains("route.no_rear_cross_aisle", sShape.UnsupportedBecause);

        var returnRoute = chilled.RouteOptions.Single(o => o.Code == PickRouteCatalogue.Return);

        Assert.True(returnRoute.IsSupported);
        Assert.Null(returnRoute.UnsupportedBecause);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Puts stock in one named bin, through a receipt and a putaway the operator
    /// overrode — which is the only way to choose a bin, because choosing one is the
    /// rules engine's job and this test needs a warehouse of a particular shape.
    /// </summary>
    private async Task<(string Sku, string Bin)> StockAsync(
        string aisle,
        string bay,
        [CallerMemberName] string caller = "")
    {
        var sku = await NewItemAsync(caller);
        var bin = $"{_warehouseCode}-STOR-AMB-{aisle}-{bay}-01-A";

        var received = await Read<DockReceipt>(await PostAsync(
            _operator,
            "/api/v1/inbound/receipts",
            new { sku, quantity = 60m, toLocationCode = _dock }));

        await Read<TaskCompletion>(await PostAsync(
            _operator,
            $"/api/v1/work/tasks/{received.WorkTaskId}/complete",
            new { toLocationCode = bin, overrideReason = "test fixture" }));

        return (sku, bin);
    }

    private async Task<List<string>> StockedSkusAsync(
        string aisle,
        string bay,
        [CallerMemberName] string caller = "")
    {
        var stocked = await StockAsync(aisle, bay, caller);
        return [stocked.Sku];
    }

    /// <summary>
    /// One order, one line per SKU, in exactly the order given — which becomes the
    /// order the picks are raised in, and therefore the baseline the walk is measured
    /// against.
    /// </summary>
    private async Task ReleaseOrderAsync(params string[] skus)
    {
        var order = await Read<OrderBody>(await PostAsync(
            _supervisor,
            "/api/v1/outbound/orders",
            new
            {
                warehouseCode = _warehouseCode,
                reference = $"SO-TOUR-{Interlocked.Increment(ref _counter):D3}-{Guid.CreateVersion7():N}"[..24],
                customerName = "Test Customer BV",
                stagingLocationCode = _bench,
                lines = skus.Select(sku => new { sku, quantity = 10m }).ToArray()
            }));

        var released = await Read<ReleaseResult>(await PostAsync(
            _supervisor, $"/api/v1/outbound/orders/{order.Id}/release", new { }));

        Assert.Equal(0m, released.ShortfallQuantity);
        Assert.Equal(skus.Length, released.TasksRaised);
    }

    private async Task<string> NewItemAsync(string caller)
    {
        var sku = $"TOUR-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 20)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Pick tour test item for {caller}",
            BaseUom = "EA",
            AbcClass = AbcClass.B
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

    private async Task<ZoneBody> ZoneAsync(string code) =>
        (await GetAsync<List<ZoneBody>>("/api/v1/zones")).Single(z => z.Code == code);

    private async Task DrainThePickQueueAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<WorkDbContext>();

        await work.WorkTasks
            .Where(t => t.Kind == WorkTaskKind.Pick && t.Status == WorkTaskStatus.Ready)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.Status, WorkTaskStatus.Cancelled)
                .SetProperty(x => x.CancelledReason, "drained by PickTourTests"));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

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

        await EnsureAsync("tour-supervisor", "TUR-SUP", Roles.Supervisor);
        await EnsureAsync("tour-operator", "TUR-OP1", Roles.Operator);
        await EnsureAsync("tour-operator-2", "TUR-OP2", Roles.Operator);

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

    private sealed record ProblemBody(string Code, string Detail);

    private sealed record DockReceipt(string Sku, string? SuggestedLocationCode, Guid? WorkTaskId);

    private sealed record OrderBody(Guid Id, string Reference, string Status);

    private sealed record ReleaseResult(int AllocationsRaised, int TasksRaised, decimal ShortfallQuantity);

    private sealed record TaskBody(
        Guid Id,
        string Kind,
        string Status,
        string FromLocationCode,
        Guid? PickTourId,
        int? TourSequence);

    private sealed record TaskPage(List<TaskBody> Items, int Total);

    private sealed record TaskCompletion(TaskBody Task);

    private sealed record TourSegmentBody(
        string? ZoneCode,
        string RequestedStrategy,
        string AppliedStrategy,
        string? FellBackBecause,
        int Stops);

    private sealed record TourBody(
        Guid Id,
        int StopCount,
        int Remaining,
        bool IsComplete,
        int? TravelSteps,
        int? BaselineTravelSteps,
        int? StepsSaved,
        int? AisleChanges,
        DateTimeOffset? ReleasedAt,
        int ReleasedStops,
        List<TourSegmentBody> Segments,
        List<TaskBody> Stops);

    private sealed record RouteOptionBody(string Code, bool IsSupported, string? UnsupportedBecause);

    private sealed record ZoneBody(
        Guid Id,
        string Code,
        string PickRouteStrategy,
        bool HasRearCrossAisle,
        int Aisles,
        int Bays,
        List<RouteOptionBody> RouteOptions);
}
