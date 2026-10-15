using Countflow.SharedKernel.Routing;

namespace Countflow.SharedKernel.Tests.Routing;

/// <summary>
/// The properties the routing heuristics claim about themselves.
/// <para>
/// Pure and synchronous, like the rule engine's tests and for the same reason: a
/// sequencer that could only be exercised against a live warehouse could not be
/// argued about either. Everything here is arithmetic over a grid built in four
/// lines.
/// </para>
/// </summary>
public sealed class PickRouteTests
{
    private static readonly Guid Ambient = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Chilled = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Four aisles of six bays, which is enough rack to tell the four strategies apart.</summary>
    private static IEnumerable<RouteCell> Grid(Guid zone, int aisles = 4, int bays = 6) =>
        from aisle in Enumerable.Range(1, aisles)
        from bay in Enumerable.Range(1, bays)
        select new RouteCell(zone, $"A{aisle:D2}", $"{bay:D2}");

    private static RouteStop Bin(string aisle, string bay, int pickSequence = 0, Guid? zone = null) =>
        new(Guid.CreateVersion7(), $"{aisle}-{bay}", zone ?? Ambient, ZoneSequence(zone), pickSequence, aisle, bay);

    private static int ZoneSequence(Guid? zone) => zone == Chilled ? 20 : 10;

    private static ZoneRouting Zone(Guid id, string code, string strategy, bool rear, int sequence = 10) =>
        new(id, code, sequence, strategy, rear);

    private static RouteGeometry GeometryOf(params ZoneRouting[] zones) =>
        RouteGeometry.Of(zones.SelectMany(z => Grid(z.ZoneId)), zones);

    private static IReadOnlyDictionary<Guid, ZoneRouting> Map(params ZoneRouting[] zones) =>
        zones.ToDictionary(z => z.ZoneId);

    private static string[] Codes(IEnumerable<RouteStop> stops) => [.. stops.Select(s => s.Code)];

    // ---- S-Shape ----

    /// <summary>
    /// The property the whole design turns on, and the one a sort cannot have.
    /// <para>
    /// A picker who leaves aisle 1 at the far end and finds nothing to pick in aisle 2
    /// arrives at aisle 3 <i>still at the far end</i>. The alternation therefore counts
    /// aisles that were visited, not aisles that were numbered — and because the answer
    /// for aisle 3 depends on whether aisle 2 was in the set, no key computed from a
    /// single location can produce it.
    /// </para>
    /// </summary>
    [Fact]
    public void SShape_alternates_on_visited_aisles_so_a_skipped_aisle_flips_the_parity()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: true);
        var geometry = GeometryOf(zone);

        var withoutTheSkip = PickRoute.Plan(
            [Bin("A01", "02"), Bin("A02", "02"), Bin("A02", "05"), Bin("A03", "02"), Bin("A03", "05")],
            Map(zone),
            geometry);

        // A01 outward, A02 back the other way, A03 outward again.
        Assert.Equal(
            ["A01-02", "A02-05", "A02-02", "A03-02", "A03-05"],
            Codes(withoutTheSkip.Stops));

        var withTheSkip = PickRoute.Plan(
            [Bin("A01", "02"), Bin("A03", "02"), Bin("A03", "05")],
            Map(zone),
            geometry);

        // Nothing to pick in A02, so the picker is still at the far end when they
        // reach A03 and walk it back to front. This is the aisle-length that a sort
        // by bin sequence gets wrong every time an odd number of aisles is skipped.
        Assert.Equal(["A01-02", "A03-05", "A03-02"], Codes(withTheSkip.Stops));
    }

    [Fact]
    public void SShape_falls_back_and_says_why_when_the_zone_has_no_rear_cross_aisle()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: false);

        var plan = PickRoute.Plan(
            [Bin("A01", "02", 102), Bin("A02", "05", 205)],
            Map(zone),
            GeometryOf(zone));

        var segment = Assert.Single(plan.Segments);

        Assert.Equal(PickRouteCatalogue.SShape, segment.RequestedStrategy);
        Assert.Equal(PickRouteCatalogue.SequenceOrdinal, segment.AppliedStrategy);
        Assert.Contains("route.no_rear_cross_aisle", segment.FellBackBecause);
        Assert.Single(plan.Fallbacks);
    }

    [Fact]
    public void A_zone_whose_bins_record_no_aisle_cannot_be_walked_geometrically()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        // A grid with no aisles on it: bins exist, but nobody surveyed them.
        var geometry = RouteGeometry.Of(
            [new RouteCell(Ambient, null, null), new RouteCell(Ambient, null, null)],
            [zone]);

        var plan = PickRoute.Plan(
            [new RouteStop(Guid.CreateVersion7(), "X-2", Ambient, 10, 2, null, null),
             new RouteStop(Guid.CreateVersion7(), "X-1", Ambient, 10, 1, null, null)],
            Map(zone),
            geometry);

        Assert.Equal(PickRouteCatalogue.SequenceOrdinal, plan.Segments[0].AppliedStrategy);
        Assert.Contains("route.no_aisles", plan.Segments[0].FellBackBecause);

        // Fallen back, not given up: the stops still come out in bin order.
        Assert.Equal(["X-1", "X-2"], Codes(plan.Stops));
    }

    // ---- Return ----

    [Fact]
    public void Return_goes_aisle_by_aisle_and_shallow_to_deep()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var plan = PickRoute.Plan(
            [Bin("A02", "05"), Bin("A01", "04"), Bin("A02", "01"), Bin("A01", "01")],
            Map(zone),
            GeometryOf(zone));

        Assert.Equal(["A01-01", "A01-04", "A02-01", "A02-05"], Codes(plan.Stops));
    }

    /// <summary>
    /// Return needs no rear cross-aisle. That is the entire reason it exists, and it
    /// is what a hall that cannot honestly claim one should be running.
    /// </summary>
    [Fact]
    public void Return_is_available_to_a_hall_with_one_cross_aisle()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        Assert.Null(GeometryOf(zone).Explain(Ambient, new ReturnRoute().Requires));
    }

    // ---- Midpoint ----

    [Fact]
    public void Midpoint_takes_the_front_halves_out_and_the_back_halves_home()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Midpoint, rear: true);

        // Six bays, so the midpoint is depth 3: bays 01–03 are the front half and
        // 04–06 the back.
        var plan = PickRoute.Plan(
            [Bin("A01", "01"), Bin("A01", "06"), Bin("A03", "02"), Bin("A03", "05")],
            Map(zone),
            GeometryOf(zone));

        Assert.Equal(
            ["A01-01", "A03-02", "A03-05", "A01-06"],
            Codes(plan.Stops));
    }

    /// <summary>
    /// The midpoint is the middle of the aisle, not the middle of the picks in it —
    /// which is why the grid is built from the zone and not from the stops. Ranked
    /// over these two bins alone, bay 05 would be "the back of the rack" and both
    /// would end up on the homeward leg.
    /// </summary>
    [Fact]
    public void Midpoint_measures_the_aisle_and_not_the_tour()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Midpoint, rear: true);

        var plan = PickRoute.Plan(
            [Bin("A01", "04"), Bin("A01", "05")],
            Map(zone),
            RouteGeometry.Of(Grid(Ambient, aisles: 4, bays: 12), [zone]));

        // Twelve bays makes the midpoint depth 6, so both of these are in the front
        // half and are walked outward, shallow first.
        Assert.Equal(["A01-04", "A01-05"], Codes(plan.Stops));
    }

    // ---- Sequence ordinal ----

    /// <summary>
    /// A bay's two positions share a pick sequence, because position is not in the
    /// composite. Without a second key the same set comes out in a different order on
    /// two runs, which makes a tour irreproducible and this test impossible to write.
    /// </summary>
    [Fact]
    public void Sequence_ordinal_breaks_ties_so_the_same_set_always_comes_out_the_same_way()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.SequenceOrdinal, rear: false);

        var a = new RouteStop(Guid.CreateVersion7(), "BIN-B", Ambient, 10, 500, "A01", "05");
        var b = new RouteStop(Guid.CreateVersion7(), "BIN-A", Ambient, 10, 500, "A01", "05");

        Assert.Equal(
            ["BIN-A", "BIN-B"],
            Codes(PickRoute.Plan([a, b], Map(zone), GeometryOf(zone)).Stops));

        Assert.Equal(
            ["BIN-A", "BIN-B"],
            Codes(PickRoute.Plan([b, a], Map(zone), GeometryOf(zone)).Stops));
    }

    // ---- Zones ----

    /// <summary>
    /// Zone first, always. A picker who crosses between zones twice has walked the
    /// building twice, which is the sentence <c>RouteScale</c> is built on and the
    /// promise <c>Zone.PickSequence</c> has been making since Phase 1.
    /// </summary>
    [Fact]
    public void A_tour_is_cut_into_zone_segments_in_zone_order_and_each_gets_its_own_strategy()
    {
        var ambient = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: true, sequence: 10);
        var chilled = Zone(Chilled, "CHL", PickRouteCatalogue.Return, rear: false, sequence: 20);

        var geometry = GeometryOf(ambient, chilled);

        var plan = PickRoute.Plan(
            [
                Bin("A02", "03", zone: Chilled),
                Bin("A02", "04"),
                Bin("A01", "01", zone: Chilled),
                Bin("A01", "02")
            ],
            Map(ambient, chilled),
            geometry);

        Assert.Equal(
            ["A01-02", "A02-04", "A01-01", "A02-03"],
            Codes(plan.Stops));

        Assert.Equal(["AMB", "CHL"], [.. plan.Segments.Select(s => s.ZoneCode ?? "?")]);
        Assert.Equal(PickRouteCatalogue.SShape, plan.Segments[0].AppliedStrategy);
        Assert.Equal(PickRouteCatalogue.Return, plan.Segments[1].AppliedStrategy);
        Assert.Empty(plan.Fallbacks);
    }

    // ---- The grid ----

    /// <summary>
    /// Ranked, never parsed. The aisle's ordinal is its place among the codes that
    /// exist, so a warehouse that numbers its aisles 1, 5 and 9 gets ranks 0, 1 and 2
    /// rather than a gap nobody walks.
    /// </summary>
    [Fact]
    public void An_aisle_is_ranked_among_the_codes_that_exist_rather_than_parsed()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var geometry = RouteGeometry.Of(
            [
                new RouteCell(Ambient, "A01", "01"),
                new RouteCell(Ambient, "A05", "01"),
                new RouteCell(Ambient, "A09", "02")
            ],
            [zone]);

        Assert.Equal(0, geometry.AisleRank(Bin("A01", "01")));
        Assert.Equal(1, geometry.AisleRank(Bin("A05", "01")));
        Assert.Equal(2, geometry.AisleRank(Bin("A09", "01")));
        Assert.Equal(3, geometry.AisleCount(Ambient));
    }

    [Fact]
    public void A_zone_with_one_bay_code_has_no_depth_to_route_on()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var geometry = RouteGeometry.Of(
            [new RouteCell(Ambient, "A01", "01"), new RouteCell(Ambient, "A02", "01")],
            [zone]);

        Assert.Contains("route.no_bay_depth", geometry.Explain(Ambient, new ReturnRoute().Requires));
    }

    // ---- Travel ----

    [Fact]
    public void Travel_within_an_aisle_is_the_difference_in_depth()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var travel = GeometryOf(zone).Travel([Bin("A01", "01"), Bin("A01", "05")]);

        Assert.True(travel.IsMeasured);
        Assert.Equal(4, travel.Steps);
        Assert.Equal(0, travel.AisleChanges);
    }

    /// <summary>
    /// Out to a cross-aisle, along it, and back in — using one cross-aisle for both
    /// halves, so the two ends are minimised together rather than independently.
    /// </summary>
    [Fact]
    public void Crossing_between_aisles_uses_the_cheaper_cross_aisle_when_there_are_two()
    {
        var withRear = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: true);
        var withoutRear = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        // Two deep bins (depth 4 of 0..5) in neighbouring aisles. Out of the back it
        // is 1 + 1; out of the front it would be 4 + 4.
        RouteStop[] walk = [Bin("A01", "05"), Bin("A02", "05")];

        Assert.Equal(
            RouteGeometry.AisleWidthInBays + 2,
            GeometryOf(withRear).Travel(walk).Steps);

        Assert.Equal(
            RouteGeometry.AisleWidthInBays + 8,
            GeometryOf(withoutRear).Travel(walk).Steps);
    }

    /// <summary>
    /// A walk with a stop the grid cannot place is unknown rather than partly known.
    /// A figure measured over the half of the walk that had geometry is precisely the
    /// number somebody would go on to believe.
    /// </summary>
    [Fact]
    public void Travel_is_unmeasured_when_a_stop_records_no_bay()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var travel = GeometryOf(zone).Travel(
            [Bin("A01", "01"), new RouteStop(Guid.CreateVersion7(), "X", Ambient, 10, 9, "A01", null)]);

        Assert.False(travel.IsMeasured);
    }

    [Fact]
    public void One_stop_is_a_measured_zero_rather_than_an_unknown()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var travel = GeometryOf(zone).Travel([Bin("A01", "01")]);

        Assert.True(travel.IsMeasured);
        Assert.Equal(0, travel.Steps);
    }

    [Fact]
    public void SShape_travel_uses_the_rear_crossing_after_its_first_visited_aisle()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: true);
        var plan = PickRoute.Plan([Bin("A01", "01"), Bin("A03", "01")], Map(zone), GeometryOf(zone));
        Assert.Equal(14, plan.Travel.Steps);
        Assert.Equal(-10, plan.StepsSaved);
    }

    [Fact]
    public void Return_travel_uses_the_front_even_when_a_rear_cross_aisle_exists()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: true);
        var plan = PickRoute.Plan([Bin("A01", "05"), Bin("A02", "05")], Map(zone), GeometryOf(zone));
        Assert.Equal(10, plan.Travel.Steps);
    }

    // ---- The instrument ----

    /// <summary>
    /// The reason the cost model exists at all. A heuristic nobody can check is a
    /// heuristic still running years after it stopped helping, and there is no
    /// <c>RuleInfluence</c> for a thing that produces no ranking.
    /// </summary>
    [Fact]
    public void A_plan_reports_what_it_saved_against_the_order_the_stops_arrived_in()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.SShape, rear: true);

        // Order-line order: two aisles apart, then back, then out again. The walk a
        // release would have produced if nothing sequenced it.
        var plan = PickRoute.Plan(
            [Bin("A01", "01"), Bin("A03", "01"), Bin("A01", "05"), Bin("A03", "05")],
            Map(zone),
            GeometryOf(zone));

        Assert.NotNull(plan.StepsSaved);
        Assert.True(
            plan.StepsSaved > 0,
            $"expected the sequenced walk to be shorter, got {plan.Travel.Steps} against {plan.Baseline.Steps}");
    }

    /// <summary>
    /// Negative is a real answer and is not clamped. A strategy that costs more than
    /// doing nothing is the finding; a floor of zero is how a bad setting survives.
    /// </summary>
    [Fact]
    public void Steps_saved_is_null_rather_than_zero_when_the_walk_cannot_be_measured()
    {
        var zone = Zone(Ambient, "AMB", PickRouteCatalogue.Return, rear: false);

        var plan = PickRoute.Plan(
            [
                Bin("A01", "01"),
                new RouteStop(Guid.CreateVersion7(), "X", Ambient, 10, 9, null, null)
            ],
            Map(zone),
            GeometryOf(zone));

        Assert.Null(plan.StepsSaved);
    }

    // ---- The catalogue ----

    [Fact]
    public void An_unregistered_strategy_code_is_reported_rather_than_silently_defaulted()
    {
        var zone = Zone(Ambient, "AMB", "route.invented_last_tuesday", rear: true);

        var plan = PickRoute.Plan([Bin("A01", "01")], Map(zone), GeometryOf(zone));

        Assert.Equal(PickRouteCatalogue.SequenceOrdinal, plan.Segments[0].AppliedStrategy);
        Assert.Contains("route.unknown_strategy", plan.Segments[0].FellBackBecause);
        Assert.False(PickRouteCatalogue.Knows("route.invented_last_tuesday"));
    }

    [Fact]
    public void Every_registered_strategy_has_a_distinct_code()
    {
        Assert.Equal(
            PickRouteCatalogue.All.Count,
            PickRouteCatalogue.All.Select(s => s.Code).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_empty_walk_plans_to_nothing_rather_than_throwing()
    {
        var plan = PickRoute.Plan([], Map(), RouteGeometry.Empty);

        Assert.Empty(plan.Stops);
        Assert.Empty(plan.Segments);
        Assert.Null(plan.StepsSaved);
    }
}
