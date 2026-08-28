namespace Countflow.SharedKernel.Rules;

/// <summary>
/// The kind of choice a rule set makes. One rule set is active per kind per
/// warehouse; everything else about a rule set is data (ADR 0004).
/// </summary>
public enum DecisionKind
{
    /// <summary>Which location a received pallet goes to. The first one, and Phase 3's.</summary>
    Putaway = 1,

    /// <summary>Which stock satisfies an order line. Phase 4.</summary>
    Allocation = 2,

    /// <summary>How orders group into a wave. Phase 4.</summary>
    Waving = 3,

    /// <summary>Which operator gets the next task. Phase 3 assigns by hand; the rule set comes later.</summary>
    TaskAssignment = 4,

    /// <summary>
    /// Which stock to count, and when. Phase 5, and the third vocabulary.
    /// <para>
    /// The first decision kind whose subject is the record rather than the goods.
    /// Putaway and allocation both answer "where do these units go or come from";
    /// this one answers "which of the numbers we are publishing do we least
    /// believe", which is a question about the system's own confidence — and the
    /// reason it is a rule set rather than a nightly job is that the answer is
    /// entirely a matter of policy and every warehouse's policy is different.
    /// </para>
    /// </summary>
    CycleCount = 5,

    /// <summary>
    /// What becomes of a unit that came back. Phase 7, and the fourth vocabulary.
    /// <para>
    /// The first decision kind whose candidates are not <i>things</i>. Putaway
    /// ranks bins, allocation and counting rank holdings; this ranks four fixed
    /// outcomes — restock, quarantine, repair, scrap — which exist only as a
    /// vocabulary and have no row anywhere. That is what makes it the real test of
    /// <c>decision 11</c>'s claim: if the engine can rank a closed set of verbs the
    /// same way it ranks two thousand shelves, "a new vocabulary needs no change to
    /// the kernel" is a property rather than a coincidence.
    /// </para>
    /// <para>
    /// It is also the first field small enough that <see cref="RuleInfluence"/>
    /// tells you very little about it. See
    /// <c>docs/decisions.md § 12</c>, where that is accepted rather than worked
    /// around.
    /// </para>
    /// </summary>
    Disposition = 6
}

/// <summary>
/// Something a rule set can choose between.
/// <para>
/// The two identifiers are both needed and neither replaces the other. <see
/// cref="Id"/> is what the decision is stored against and what the caller acts
/// on; <see cref="Key"/> is what the trace shows a human — a location code, not
/// a GUID. A trace nobody can read explains nothing.
/// </para>
/// </summary>
public interface IRuleCandidate
{
    Guid Id { get; }

    string Key { get; }
}

/// <summary>
/// Which rule set produced a decision, pinned at the version that was live when
/// it ran.
/// <para>
/// Recorded on every trace rather than resolved on read. Rule sets are promotable
/// data, so "why did it send them there" asked next month has to be answered by
/// the rules as they stood at the time, not as they stand now.
/// </para>
/// </summary>
public sealed record RuleSetStamp(Guid Id, string Name, int Version);
