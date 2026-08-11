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
    TaskAssignment = 4
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
