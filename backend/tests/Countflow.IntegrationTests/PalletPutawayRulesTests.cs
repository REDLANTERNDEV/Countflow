using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.IntegrationTests;

// Pure rule tests: no database fixture. A pallet must be evaluated as one load,
// while the existing single-item vocabulary keeps its configured order/weights.
public sealed class PalletPutawayRulesTests
{
    [Theory]
    [InlineData("putaway.volume_capacity")]
    [InlineData("putaway.weight_capacity")]
    public void Two_individually_fitting_items_can_exceed_the_pallet_capacity(string code)
    {
        var bin = Bin() with { MaxVolumeM3 = 10m, MaxWeightKg = 10m };
        var lines = new[] { Line(Item(), bin, 6m), Line(Item(), bin, 6m) };
        var result = Evaluate(code, bin, lines);
        Assert.Null(result.Winner);
        Assert.Equal(0, result.AdmissibleCount);
    }

    [Fact]
    public void An_empty_single_sku_bin_cannot_accept_two_incoming_skus()
    {
        var bin = Bin() with { MaxDistinctItems = 1 };
        Assert.Null(Evaluate("putaway.distinct_item_limit", bin,
            [Line(Item(), bin), Line(Item(), bin)]).Winner);
    }

    [Fact]
    public void Distinct_items_are_a_union_with_the_bin_not_a_sum_of_pallet_lines()
    {
        var bin = Bin() with { MaxDistinctItems = 2 };
        var first = Item();
        var second = Item();
        var same = new LocationOccupancy(bin.Id, 1, 5m, 5m, 5m, 5m, true, false);
        var other = same with { SubjectItemUnits = 0m, HoldsSubjectLot = false };
        var lines = new[]
        {
            Line(first, bin, occupancy: same),
            Line(first, bin, lot: Guid.NewGuid(), occupancy: same),
            Line(second, bin, occupancy: other)
        };
        Assert.NotNull(Evaluate("putaway.distinct_item_limit", bin, lines).Winner);
        Assert.Null(Evaluate("putaway.distinct_item_limit", bin with { MaxDistinctItems = 1 }, lines).Winner);
    }

    [Fact]
    public void Every_incoming_temperature_must_match_the_destination()
    {
        var bin = Bin();
        Assert.Null(Evaluate("putaway.temperature_match", bin,
            [Line(Item(), bin), Line(Item() with { TemperatureBand = TemperatureBand.Chilled }, bin)]).Winner);
    }

    [Fact]
    public void Incoming_lots_cannot_commingle_even_when_the_destination_is_empty()
    {
        var bin = Bin();
        var item = Item() with { IsLotTracked = true };
        Assert.Null(Evaluate("putaway.lot_purity", bin,
            [Line(item, bin, lot: Guid.NewGuid()), Line(item, bin, lot: Guid.NewGuid())]).Winner);
    }

    [Fact]
    public void A_conflicting_existing_lot_of_the_second_item_excludes_the_bin()
    {
        var bin = Bin();
        var held = new LocationOccupancy(bin.Id, 1, 5m, 5m, 5m, 5m, false, true);
        Assert.Null(Evaluate("putaway.lot_purity", bin,
            [Line(Item(), bin), Line(Item() with { IsLotTracked = true }, bin,
                lot: Guid.NewGuid(), occupancy: held)]).Winner);
    }

    [Fact]
    public void An_unknown_incoming_dimension_remains_unknown_instead_of_a_partial_total()
    {
        var bin = Bin() with { MaxVolumeM3 = 1m, MaxWeightKg = 1m };
        var lines = new[] { Line(Item(), bin, 5m), Line(Item() with { UnitVolumeM3 = null, UnitWeightKg = null }, bin) };
        // Existing capacity policy admits unknown requirements; it must not use
        // the known item's partial total as though it described the whole load.
        Assert.NotNull(Evaluate("putaway.volume_capacity", bin, lines).Winner);
        Assert.NotNull(Evaluate("putaway.weight_capacity", bin, lines).Winner);
    }

    [Fact]
    public void Velocity_weights_skus_equally_rather_than_comparing_unrelated_base_units()
    {
        var bin = Bin() with { Role = StorageRole.Forward };
        var a = Item() with { AbcBand = AbcBand.A };
        var c = Item() with { AbcBand = AbcBand.C };
        var result = Evaluate("putaway.velocity_slotting", bin,
            [Line(a, bin, 1000m), Line(c, bin, 1m), Line(c, bin, 9m)]);
        Assert.Equal(0.6m, result.Winner!.Score);
    }

    [Fact]
    public void Consolidation_averages_lots_within_each_sku_before_averaging_skus()
    {
        var bin = Bin();
        var a = Item();
        var b = Item();
        var held = new LocationOccupancy(bin.Id, 2, 10m, 10m, 10m, 5m, true, false);
        var otherLot = held with { HoldsSubjectLot = false };
        var unrelated = held with { SubjectItemUnits = 0m, HoldsSubjectLot = false };
        // SKU A: 1/4 * 1 + 3/4 * .8 = .85; SKU B: 0; equal SKU mean .425.
        var lines = new[] { Line(a, bin, 1m, Guid.NewGuid(), held), Line(a, bin, 3m, Guid.NewGuid(), otherLot), Line(b, bin, 100m, occupancy: unrelated) };
        var forward = Evaluate("putaway.consolidate_same_item", bin, lines);
        var reverse = Evaluate("putaway.consolidate_same_item", bin, [.. lines.Reverse()]);
        Assert.Equal(0.425m, forward.Winner!.Score);
        Assert.Equal(forward.Winner.Score, reverse.Winner!.Score);
    }

    [Fact]
    public void Cube_utilisation_uses_the_whole_load_once()
    {
        var bin = Bin() with { MaxVolumeM3 = 10m };
        Assert.Equal(0.8m, Evaluate("putaway.cube_utilisation", bin,
            [Line(Item(), bin, 3m), Line(Item(), bin, 5m)]).Winner!.Score);
    }

    [Fact]
    public void A_single_line_keeps_the_original_rejection_explanation()
    {
        var bin = Bin() with { MaxDistinctItems = 1 };
        var occupied = new LocationOccupancy(bin.Id, 1, 5m, 5m, 5m, 0m, false, false);
        var line = Line(Item(), bin, occupancy: occupied);
        var set = Set("putaway.distinct_item_limit");
        var old = PutawayRuleCatalogue.Build(set).Value;
        var pallet = PalletPutawayRuleCatalogue.Build(set).Value;
        var before = old.Constraints.Single().Exclude(bin, line);
        var after = pallet.Constraints.Single().Exclude(bin, new PalletPutawayContext([line]));
        Assert.NotNull(before);
        Assert.Equivalent(before, after);
    }

    [Fact]
    public void A_single_line_matches_the_existing_single_item_evaluation()
    {
        var bin = Bin();
        var line = Line(Item(), bin, 3m);
        var set = Set(PutawayRuleCatalogue.ConstraintCodes.Concat(PutawayRuleCatalogue.PreferenceCodes).ToArray());
        var old = PutawayRuleCatalogue.Build(set).Value;
        var pallet = PalletPutawayRuleCatalogue.Build(set).Value;
        var before = RuleEngine.Evaluate(DecisionKind.Putaway, old.Stamp, new[] { bin }, line, old.Constraints, old.Preferences);
        var after = RuleEngine.Evaluate(DecisionKind.Putaway, pallet.Stamp, new[] { bin }, new PalletPutawayContext([line]), pallet.Constraints, pallet.Preferences);
        Assert.Equal(before.Winner!.Score, after.Winner!.Score);
        Assert.Equivalent(before.Winner.Scores, after.Winner.Scores);
    }

    private static Decision<StorageLocationSnapshot> Evaluate(string code, StorageLocationSnapshot bin, IReadOnlyList<PutawayContext> lines)
    {
        var rules = PalletPutawayRuleCatalogue.Build(Set(code)).Value;
        return RuleEngine.Evaluate(DecisionKind.Putaway, rules.Stamp, new[] { bin },
            new PalletPutawayContext(lines), rules.Constraints, rules.Preferences);
    }

    private static RuleSet Set(params string[] codes)
    {
        var set = new RuleSet { Kind = DecisionKind.Putaway, Name = "pallet test", Version = 1 };
        foreach (var code in codes)
            set.Rules.Add(new RuleDefinition { RuleSetId = set.Id, Code = code,
                Role = PutawayRuleCatalogue.ConstraintCodes.Contains(code) ? RuleRole.Constraint : RuleRole.Preference,
                Weight = 1m, Order = set.Rules.Count });
        return set;
    }

    private static StorageLocationSnapshot Bin() => new(Guid.NewGuid(), Guid.NewGuid(), "BAY", null, null,
        TemperatureBand.Ambient, StorageRole.Reserve, true, true, true, 0, 0, null, null, null);

    private static ItemSnapshot Item() => new(Guid.NewGuid(), "SKU", "Item", "ea", false, false, false,
        true, AbcBand.B, TemperatureBand.Ambient, false, 1m, 1m);

    private static readonly Guid Origin = Guid.NewGuid();

    private static PutawayContext Line(ItemSnapshot item, StorageLocationSnapshot bin, decimal qty = 1m,
        Guid? lot = null, LocationOccupancy? occupancy = null) => new(item, lot, qty,
        StockCondition.Available, Origin, occupancy is null ? new Dictionary<Guid, LocationOccupancy>() :
            new Dictionary<Guid, LocationOccupancy> { [bin.Id] = occupancy }, item.UnitVolumeM3 * qty,
        item.UnitWeightKg * qty, RouteScale.Of([bin]));
}
