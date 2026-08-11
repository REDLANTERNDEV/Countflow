using Countflow.Modules.Inventory.Domain;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// One side of a movement: <c>(location, licence plate, status)</c>.
/// <para>
/// A value type on purpose. "The stock in A-07-03-02" is not a thing with an
/// identity that can be edited; it is a coordinate, and two coordinates with the
/// same three components are the same coordinate. Modelling it as a struct means
/// the equality the ledger relies on comes free and cannot drift.
/// </para>
/// </summary>
public readonly record struct Holding(
    Guid LocationId,
    Guid? LicensePlateId,
    InventoryStatus Status)
{
    /// <summary>
    /// A stable ordering key.
    /// <para>
    /// This is what prevents deadlocks. Two movements posted at the same instant
    /// that touch the same two balance rows in opposite directions — a transfer
    /// A→B racing a transfer B→A — would each hold the lock the other wants. Both
    /// transactions sort their rows by this string first, so every writer in the
    /// system acquires locks in one global order and a cycle cannot form.
    /// </para>
    /// </summary>
    public string LockOrderKey =>
        $"{LocationId:D}|{LicensePlateId:D}|{(int)Status:D3}";
}
