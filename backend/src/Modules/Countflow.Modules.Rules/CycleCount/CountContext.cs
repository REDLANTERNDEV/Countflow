using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.CycleCount;

/// <summary>
/// Everything the counting rules are allowed to know, gathered once before
/// evaluation starts.
/// <para>
/// The same discipline as the other two contexts, and the third demonstration that
/// the discipline is what makes the engine reusable: the rules are a pure function
/// of this, so Phase 6 can replay a candidate counting policy over a year of
/// history without a database in the loop.
/// </para>
/// <para>
/// What is worth noticing is the shape of what it carries. Putaway's context is
/// built around a building; allocation's around a demand and a set of dates. This
/// one is built around <b>when things last happened</b> — the only decision in the
/// system whose inputs are almost entirely timestamps, because it is the only one
/// choosing on the basis of how stale a belief is rather than what is true.
/// </para>
/// </summary>
/// <param name="OnHandByItem">
/// Total on hand of each item across every candidate. The denominator of
/// <see cref="ShareOfItemStock"/>, and gathered here rather than computed in the
/// rule because a preference that summed the field would be a rule that knew about
/// the other candidates.
/// </param>
/// <param name="FurthestPickSequence">
/// The largest route position <b>in the warehouse</b>, exactly as
/// <c>PutawayContext</c> uses. Not the largest among the candidates — see
/// <see cref="NormalisedDistance"/>, where that distinction is load-bearing and
/// was got wrong once already.
/// </param>
public sealed record CountContext(
    DateTimeOffset Now,
    IReadOnlyDictionary<Guid, ItemSnapshot> Items,
    IReadOnlyDictionary<Guid, StorageLocationSnapshot> Locations,
    IReadOnlySet<Guid> AlreadyScheduled,
    IReadOnlyDictionary<Guid, decimal> OnHandByItem,
    long FurthestPickSequence)
{
    public ItemSnapshot? ItemOf(CountCandidate candidate) =>
        Items.GetValueOrDefault(candidate.ItemId);

    public StorageLocationSnapshot? LocationOf(CountCandidate candidate) =>
        Locations.GetValueOrDefault(candidate.LocationId);

    /// <summary>
    /// Days since anybody verified this holding, or null if nobody ever has.
    /// <para>
    /// Null rather than a very large number, deliberately. "Never counted" and
    /// "counted eight hundred days ago" are the same urgency and different facts,
    /// and a rule that wants to say <i>never</i> in the trace has to be able to
    /// tell them apart.
    /// </para>
    /// </summary>
    public int? DaysSinceCount(CountCandidate candidate) =>
        candidate.LastCountedAt is { } last
            ? (int)Math.Max(0d, (Now - last).TotalDays)
            : null;

    public double HoursSinceMovement(CountCandidate candidate) =>
        Math.Max(0d, (Now - candidate.LastMovedAt).TotalHours);

    /// <summary>
    /// How much of everything the warehouse holds of this item sits in this one
    /// holding, on <c>[0, 1]</c>.
    /// <para>
    /// The exposure a wrong number here would represent. An error on a holding that
    /// is the item's entire stock is an error on the whole SKU; the same error on
    /// one of forty pallets is a rounding difference on the item's position, and the
    /// warehouse would rather spend the count elsewhere.
    /// </para>
    /// <para>
    /// Normalised against the item's <i>own</i> total rather than against the field
    /// of candidates, which is the same choice FEFO makes and for the same reason —
    /// see the note on <c>AllocationContext.RotationScore</c> before changing it.
    /// A field-relative measure would rank a warehouse's largest holdings first
    /// regardless of item, so the four-hundred-unit bin of a fast-moving C-class
    /// consumable would permanently outrank the single pallet that is a slow item's
    /// entire stock. The denominator here is a real quantity that means something on
    /// its own — everything we have of this SKU — which is exactly what a relative
    /// window is not.
    /// </para>
    /// <para>
    /// A sole holding scores 1, which is both arithmetically right and the answer
    /// we want: it <i>is</i> all of it.
    /// </para>
    /// </summary>
    public decimal ShareOfItemStock(CountCandidate candidate)
    {
        var total = OnHandByItem.GetValueOrDefault(candidate.ItemId);

        return total <= 0m
            ? 0m
            : Math.Clamp(candidate.OnHandQuantity / total, 0m, 1m);
    }

    /// <summary>
    /// Route position normalised onto <c>[0, 1]</c> against the whole warehouse,
    /// zone first — the front of the building is 0, the far end is 1.
    /// <para>
    /// <b>The warehouse, not the candidate field, and this is the third time the
    /// distinction has drawn blood.</b> Putaway measures against the building
    /// because it is choosing among two thousand bins; allocation measures across
    /// the handful of holdings one SKU happens to be in, because measuring four
    /// bins in one aisle against a twenty-four-aisle hall scores them identically
    /// and says nothing. Counting looks like allocation — a short list of holdings —
    /// and is really putaway: it is choosing among the whole building, and the list
    /// is short only because the constraints have already refused most of it.
    /// </para>
    /// <para>
    /// Normalising against the field was tried and is wrong, and the container run
    /// is what showed it. Four admissible holdings in two adjacent bays came back
    /// scored 0% and 100% along the route — a few metres stretched across the full
    /// width of the score — so travel contributed a 0.150 swing while the exposure
    /// rule it is meant to defer to contributed 0.014, and two bays you can see
    /// from each other were ranked as though they were at opposite ends of the
    /// warehouse. It is the same failure Phase 4 found in allocation's travel rule
    /// and in FEFO, arrived at from the other side: there, a degenerate field
    /// collapsed to nothing; here, a narrow one is stretched to everything.
    /// </para>
    /// <para>
    /// Measuring against the building also keeps counting and putaway agreeing
    /// about which end of it is near, which matters — a warehouse whose two rule
    /// sets disagreed about that would send counters against the traffic.
    /// </para>
    /// </summary>
    public decimal NormalisedDistance(StorageLocationSnapshot location)
    {
        if (FurthestPickSequence <= 0)
        {
            return 0m;
        }

        var position = ((long)location.ZonePickSequence * 1_000_000) + location.PickSequence;

        return Math.Clamp((decimal)position / FurthestPickSequence, 0m, 1m);
    }
}
