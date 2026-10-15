namespace Countflow.Modules.Work.Domain;

/// <summary>
/// A set of picks claimed together and walked in an order.
/// <para>
/// <b>Why this is a row and not two columns.</b> The queue hands work out with
/// <c>FOR UPDATE SKIP LOCKED</c>, which steps over rows a rival is taking. Sequence
/// a pre-built list of tasks and then let two pickers claim from it that way and each
/// gets whatever they could lock, with holes — and a walk over aisles 1, 3 and 5 is
/// not the walk that was computed over 1 to 5. Both pickers then walk badly, which is
/// the exact failure sequencing exists to fix, moved one level up and made invisible.
/// The unit of mutual exclusion has to be the unit of sequencing, and under
/// <c>SKIP LOCKED</c> the unit of mutual exclusion is a row.
/// </para>
/// <para>
/// <b>So the tour is built at claim time, from what was actually locked.</b> Nothing
/// is sequenced in advance and left waiting to go stale; the picks are taken first
/// and ordered second, over the set that came back. Holes are therefore not merely
/// tolerated, they are impossible — a tour is by construction a sequence over its own
/// membership.
/// </para>
/// <para>
/// <b>What it stores is a claim and a stamp, and not a status.</b> Whether the tour
/// is finished is derivable from its tasks, and a second stored answer to "is this
/// done" is a second answer to drift — the argument
/// <c>SalesOrderLine.AllocatedQuantity</c> and <c>WorkTask.Quantity</c> have each
/// already made about running totals. What is <i>not</i> derivable is which strategy
/// produced the walk, because a zone's setting is a plain column with no promotion
/// lifecycle behind it and will have moved on. That is stamped here for the reason a
/// <c>LabelPrint</c> stamps its template's name and version: somebody is holding the
/// pick list.
/// </para>
/// </summary>
public sealed class PickTour
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid WarehouseId { get; init; }

    public required Guid AssignedToUserId { get; init; }

    public required string AssignedToUserName { get; init; }

    public required DateTimeOffset ClaimedAt { get; init; }

    public string? ClaimIdempotencyKey { get; init; }

    public string? ReleaseIdempotencyKey { get; set; }

    /// <summary>
    /// How many stops were claimed. Kept even after some are handed back, because
    /// "twelve claimed, seven walked, five returned" is the shape of an abandoned
    /// tour and counting the survivors cannot tell you it happened.
    /// </summary>
    public required int StopCount { get; init; }

    /// <summary>
    /// What the walk costs in the proxy units described on <c>RouteTravel</c>. Null
    /// when some stop on it records no aisle or no bay: the cost is then unknown
    /// rather than partially known, and publishing a figure measured over the half of
    /// the walk that had geometry is how a number nobody should trust gets trusted.
    /// </summary>
    public int? TravelSteps { get; set; }

    /// <summary>
    /// What the same stops would have cost in the order the queue would have handed
    /// them out — which, since picks are raised one per commitment inside one release,
    /// is the order the customer typed their order lines in.
    /// <para>
    /// This is the instrument, and it is the reason the cost model exists. A
    /// heuristic nobody can check is a heuristic still running years after it stopped
    /// helping; <c>RuleInfluence</c> is the same argument for the parts of the system
    /// that do produce a ranking, and this is the part that does not.
    /// </para>
    /// </summary>
    public int? BaselineTravelSteps { get; set; }

    public int? AisleChanges { get; set; }

    /// <summary>When the operator handed back whatever they had not walked.</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>How many stops went back to the queue when they did.</summary>
    public int ReleasedStops { get; set; }

    /// <summary>What produced the walk, one row per zone it crossed.</summary>
    public ICollection<PickTourSegment> Segments { get; init; } = [];

    public ICollection<WorkTask> Tasks { get; init; } = [];
}

/// <summary>
/// One zone's share of a tour, and which strategy actually walked it.
/// <para>
/// <see cref="RequestedStrategy"/> and <see cref="AppliedStrategy"/> are stored
/// separately rather than one being inferred from the other later, for the reason
/// <c>WasOverridden</c> is stored on a task rather than derived by comparing against
/// a trace: the thing it would be compared against is mutable configuration that will
/// have moved on by the time anybody asks.
/// </para>
/// </summary>
/// <param name="FellBackBecause">
/// Null when the zone was walked the way it is set up to be. A sentence when it was
/// not — geometry the strategy needs and the zone turned out not to have. A claim is
/// never refused over it: a picker at the start of a shift being told the warehouse
/// is misconfigured and handed no work is a worse outcome than a slightly longer walk
/// somebody can read about afterwards.
/// </param>
public sealed class PickTourSegment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid PickTourId { get; init; }

    public string? ZoneCode { get; init; }

    public required string RequestedStrategy { get; init; }

    public required string AppliedStrategy { get; init; }

    public string? FellBackBecause { get; init; }

    public required int Stops { get; init; }

    public PickTour? Tour { get; init; }
}
