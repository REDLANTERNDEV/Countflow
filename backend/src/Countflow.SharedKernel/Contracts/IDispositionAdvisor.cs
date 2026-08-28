using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// What becomes of a unit that came back.
/// <para>
/// <b>Not a <see cref="StockCondition"/>, and the distinction is the whole of
/// decision 12.</b> A condition is a state a quantity is <i>in</i>; a disposition
/// is an action taken <i>on</i> it. <c>Damaged</c> is a condition and
/// <see cref="Scrap"/> is a verb, and the two coincide for exactly two of these
/// four by accident. Adding <c>StockCondition.AwaitingRepair</c> to make repair
/// fit would be the moment the two vocabularies fused; every one of these instead
/// resolves to a movement the ledger already knows how to post.
/// </para>
/// </summary>
public enum Disposition
{
    /// <summary>Back into saleable stock. A condition change to Available, then an ordinary putaway.</summary>
    Restock = 1,

    /// <summary>Held where it landed, pending somebody's judgement. A status change in place.</summary>
    Quarantine = 2,

    /// <summary>Out to a third party and expected back. A transfer to the InTransit counterparty.</summary>
    Repair = 3,

    /// <summary>Destroyed. A write-off to the Scrap counterparty, and the only irreversible one.</summary>
    Scrap = 4
}

/// <summary>
/// One disposition, as something the engine can rank.
/// <para>
/// The first candidate in this system that is not a row. Putaway ranks
/// <see cref="StorageLocationSnapshot"/>, allocation and counting rank holdings;
/// all three have a database identity that <see cref="IRuleCandidate.Id"/> can
/// simply be. A disposition has none, so the identifiers are minted here as four
/// fixed constants and never generated — a trace written this morning and one
/// written next year have to name the same candidate by the same id, or replay
/// compares two decisions that were never about the same thing.
/// </para>
/// <para>
/// <see cref="IRuleCandidate.Key"/> is the lower-case name rather than a number,
/// for the reason every key in this system is human-readable: the trace is read by
/// a supervisor asking why a pallet was destroyed.
/// </para>
/// </summary>
public sealed record DispositionOption(Disposition Disposition) : IRuleCandidate
{
    // Fixed, and fixed by hand. A deterministic hash of the enum name would look
    // tidier and would silently change the day somebody renamed a member, which is
    // exactly the kind of drift a trace cannot survive.
    private static readonly IReadOnlyDictionary<Disposition, Guid> Ids =
        new Dictionary<Disposition, Guid>
        {
            [Disposition.Restock] = new("8f3d1c60-4a25-4a1e-9a37-3f0f5f3c9b01"),
            [Disposition.Quarantine] = new("8f3d1c60-4a25-4a1e-9a37-3f0f5f3c9b02"),
            [Disposition.Repair] = new("8f3d1c60-4a25-4a1e-9a37-3f0f5f3c9b03"),
            [Disposition.Scrap] = new("8f3d1c60-4a25-4a1e-9a37-3f0f5f3c9b04")
        };

    /// <summary>
    /// The whole field, always in this order.
    /// <para>
    /// Every evaluation considers all four. Pre-filtering to the ones that look
    /// plausible would be the <c>WHERE</c> clause
    /// <see cref="ILocationDirectory.FindStorageCandidatesAsync"/> refuses to be:
    /// a candidate excluded by a query never appears in the trace, and "why wasn't
    /// it just repaired?" is the question this decision exists to answer.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<DispositionOption> All =
    [
        new(Disposition.Restock),
        new(Disposition.Quarantine),
        new(Disposition.Repair),
        new(Disposition.Scrap)
    ];

    public Guid Id => Ids[Disposition];

    public string Key => Disposition switch
    {
        Disposition.Restock => "restock",
        Disposition.Quarantine => "quarantine",
        Disposition.Repair => "repair",
        Disposition.Scrap => "scrap",
        _ => Disposition.ToString().ToLowerInvariant()
    };
}

/// <summary>
/// What an inspector found, which is the only reason this decision waits for an
/// inspection at all.
/// <para>
/// Every field here is a judgement a person made with the goods in their hands.
/// None of it is derivable from master data or from the ledger, and that is the
/// argument for the return being two events rather than one: a disposition
/// decided at the dock door would be decided by somebody who has not opened the
/// carton.
/// </para>
/// </summary>
/// <param name="Condition">
/// What the goods actually are, as distinct from what they were booked in as. A
/// return lands held; this is the inspector saying it is fine after all, or that
/// it is not.
/// </param>
/// <param name="IsSealed">
/// Unopened and in resaleable packaging. The single fact that most decides
/// whether stock can go back on a shelf, and one no system can infer.
/// </param>
/// <param name="IsRepairable">
/// The inspector's judgement that this can be economically fixed. Deliberately
/// not an item attribute: whether a thing <i>can</i> be repaired is a property of
/// the item, whether <i>this one</i> is worth repairing is a property of the
/// damage, and only the second one decides anything.
/// </param>
public sealed record InspectionFindings
{
    public required StockCondition Condition { get; init; }

    public bool IsSealed { get; init; }

    public bool IsRepairable { get; init; }

    /// <summary>Free text from the inspector. Carried onto the movement, never parsed.</summary>
    public string? Notes { get; init; }
}

/// <summary>What the rules are being asked to decide the fate of.</summary>
/// <param name="DaysSinceDespatch">
/// How long the customer had it. Null when the return has no order behind it —
/// a blind return, which is as legitimate here as a blind receipt is at the dock.
/// </param>
public sealed record DispositionQuery
{
    public required Guid WarehouseId { get; init; }

    public required Guid ItemId { get; init; }

    public Guid? LotId { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>The returns bay the goods are standing in. Where a quarantine leaves them.</summary>
    public required Guid AtLocationId { get; init; }

    public required InspectionFindings Findings { get; init; }

    public int? DaysSinceDespatch { get; init; }

    /// <summary>The batch's expiry, when it has one. Scored against the item's own shelf life, never against the other candidates.</summary>
    public DateOnly? ExpiryDate { get; init; }

    public required PostingActor Actor { get; init; }
}

/// <summary>
/// The rules' answer, and the trace that justifies it.
/// <para>
/// <see cref="Disposition"/> is nullable and <see cref="DecisionTraceId"/> is not,
/// the same asymmetry <see cref="PutawayAdvice"/> and <see cref="CountPlanAdvice"/>
/// keep and for the same reason. A rule set is allowed to have no answer — a
/// hazardous item with no repair route and nothing left of its shelf life may
/// genuinely have every option refused — but it is never allowed to have no
/// reasoning, and "nothing was admissible, and here is what ruled each one out" is
/// the most useful thing this decision ever produces.
/// </para>
/// </summary>
public sealed record DispositionAdvice(
    Guid DecisionTraceId,
    Disposition? Disposition,
    decimal Score,
    IReadOnlyList<ScoreLine> Reasons,
    int ConsideredCount,
    int AdmissibleCount);

/// <summary>
/// The Rules module's published advisory surface for returns.
/// <para>
/// The fourth vocabulary, and the one that makes decision 11's claim testable
/// rather than merely argued. Waving was admitted on the reasoning that a new
/// vocabulary needs no change to the kernel; this is the first one built since,
/// and it is deliberately the least like the other three — a closed set of four
/// verbs with no rows behind them, scored on what a person found rather than on
/// what the warehouse holds. <c>RuleEngine</c>, <c>Decision&lt;T&gt;</c>, the
/// journal, the trace schema and the replay comparison are all untouched by it.
/// </para>
/// <para>
/// It advises and does not act. The three other advisors are consumed by callers
/// that then post a movement; this one is consumed by a caller that shows a person
/// the answer first, because one of the four candidates destroys stock and a
/// system that did that unattended would be answering a question nobody asked it.
/// </para>
/// </summary>
public interface IDispositionAdvisor
{
    Task<Result<DispositionAdvice>> AdviseAsync(
        DispositionQuery query,
        CancellationToken cancellationToken);
}
