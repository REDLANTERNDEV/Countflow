namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// Velocity classification, following the Pareto split most warehouses use:
/// roughly 20% of SKUs drive 80% of movement.
/// <para>
/// Drives two things later — slotting (A items belong nearest the despatch end
/// of the pick face) and cycle-count frequency in Phase 5 (A monthly, B
/// quarterly, C twice yearly).
/// </para>
/// </summary>
public enum AbcClass
{
    A = 1,
    B = 2,
    C = 3
}
