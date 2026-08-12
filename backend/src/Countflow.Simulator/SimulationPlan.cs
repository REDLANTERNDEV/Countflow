namespace Countflow.Simulator;

/// <summary>
/// How much warehouse to make, and how badly behaved to make it.
/// </summary>
/// <param name="Seed">
/// Everything random here comes from one seeded generator, so the same plan builds
/// the same warehouse every time.
/// <para>
/// That is not a nicety. Three scoring flaws have been found by driving a phase
/// through the containers by hand, and every one of them needed a warehouse with
/// realistic stock in it to become visible. A machine that produced a
/// <i>different</i> realistic warehouse each run would find them intermittently,
/// which is worse than not finding them: a guard that fails one run in five is a
/// guard people disable.
/// </para>
/// </param>
/// <param name="OverrideRate">
/// How often an operator puts a pallet somewhere other than where they were sent.
/// Non-zero on purpose. A warehouse where everybody follows the suggestion is one
/// where the override statistics are structurally empty and the accuracy dashboard
/// has nothing to show.
/// </param>
/// <param name="CountVarianceRate">
/// How often a count disagrees with the book. Also non-zero on purpose, and
/// deliberately small: a warehouse whose counts mostly agree is the realistic one,
/// and the clean count is the case the accuracy figure is a fraction of.
/// </param>
/// <param name="LowerCountQuietPeriod">
/// Whether to promote a warehouse-specific counting policy with the quiet period
/// set to nothing.
/// <para>
/// <c>count.settled_stock</c> will not count a bay within four hours of anything
/// moving in it, which is right — counting a bay mid-putaway measures the truck —
/// and means a warehouse built in the last ninety seconds has nothing countable at
/// all. The simulator solves that the way the product intends it to be solved: it
/// writes a rule set. The shipped default is left alone, because the new set names
/// this warehouse and a warehouse-specific set beats the shared one.
/// </para>
/// </param>
public sealed record SimulationPlan(
    string WarehouseCode,
    int Days = 4,
    int ReceiptsPerDay = 5,
    int LinesPerReceipt = 3,
    int OrdersPerDay = 3,
    int LinesPerOrder = 2,
    int CountsPerDay = 10,
    int SkuPoolSize = 40,
    int Seed = 20260812,
    double OverrideRate = 0.08,
    double CountVarianceRate = 0.2,
    bool LowerCountQuietPeriod = true);

/// <summary>What the simulated warehouse did.</summary>
/// <param name="Notes">
/// Anything that went wrong and was survived. A simulator that stopped at the first
/// refusal would mostly be a test of the API's error handling; one that ploughs on
/// and reports produces a warehouse with the ordinary amount of mess in it, which
/// is the whole point.
/// </param>
public sealed record SimulationReport(
    int Receipts,
    int Putaways,
    int Overrides,
    int Orders,
    int OrderLinesShort,
    int Picks,
    int Despatches,
    int CountPlans,
    int Counts,
    int Variances,
    IReadOnlyList<string> Notes)
{
    public static readonly SimulationReport Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    public override string ToString() =>
        $"{Receipts} receipts, {Putaways} putaways ({Overrides} overridden), {Orders} orders " +
        $"({OrderLinesShort} lines short), {Picks} picks, {Despatches} despatches, " +
        $"{CountPlans} count plans, {Counts} counts ({Variances} with a variance)";
}
