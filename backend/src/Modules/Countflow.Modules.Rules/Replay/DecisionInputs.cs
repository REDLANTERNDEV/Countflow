using Countflow.SharedKernel.Contracts;

namespace Countflow.Modules.Rules.Replay;

/// <summary>
/// What was asked of a rule set, as it is stored on the trace.
/// <para>
/// These were private records inside their advisors until Phase 6, which was right
/// while the only thing that read them was a person squinting at a JSON column.
/// They are promoted here because they now have a second reader — replay — and a
/// shape written by one class and parsed by another is a contract whether or not
/// anybody says so. Keeping them together also makes the thing they have in common
/// visible: <b>none of them stores the warehouse's state</b>. The ledger already is
/// a complete history, so occupancy, holdings and count history as of
/// <c>DecidedAt</c> are all reconstructible (ADR 0001), and storing a copy would be
/// a second version of the truth that could drift from the first.
/// </para>
/// <para>
/// They are append-only in the same sense the ledger is: a field may be added, but
/// one already written must keep its name and meaning, because a trace from last
/// quarter has to still parse. Anything absent from an older trace has to be
/// nullable and has to have a defensible default — which is why replay reports
/// fidelity per decision rather than assuming it.
/// </para>
/// </summary>
public sealed record PutawayInputs(
    Guid WarehouseId,
    Guid ItemId,
    string Sku,
    Guid? LotId,
    decimal Quantity,
    StockCondition Condition,
    Guid FromLocationId,
    Guid? LicensePlateId);

/// <summary>
/// What an allocation was asked for.
/// <para>
/// The holdings the rules saw are not stored, for the reason above. Reservations
/// were called out in Phase 4 as the one input that could <i>not</i> be
/// reconstructed that way, because they are current state rather than an
/// append-only history — see <c>WarehouseAsOf.ReservedByBalance</c>, where that
/// turns out to have been half right.
/// </para>
/// </summary>
public sealed record AllocationInputs(
    Guid WarehouseId,
    Guid ItemId,
    string Sku,
    Guid? RequiredLotId,
    decimal Quantity,
    DateOnly ShipOn);

/// <summary>
/// What a counting policy was asked.
/// <para>
/// The thinnest of the three, because almost every input to a counting decision is
/// derivable from the ledger and the count history — both append-only — so
/// replaying last month's policy against last month's warehouse needs little more
/// than the clock. <see cref="AlreadyScheduledCount"/> is the exception and the
/// reason it is a count rather than a list: what was stored is that the exclusion
/// existed, not which bays it named, so a replay knows to say its answer is
/// approximate without being able to make it exact.
/// </para>
/// </summary>
public sealed record CountPolicyInputs(
    Guid WarehouseId,
    int MaxTasks,
    int AlreadyScheduledCount,
    DateTimeOffset EvaluatedAt);
