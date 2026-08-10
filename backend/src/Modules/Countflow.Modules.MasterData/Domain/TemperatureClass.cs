namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// Storage temperature regime. An item may only be put away into a zone whose
/// class matches its own — a chilled item in an ambient aisle is spoilage, and
/// it is the putaway rules engine's first hard constraint in Phase 3.
/// </summary>
public enum TemperatureClass
{
    Ambient = 1,
    Chilled = 2,
    Frozen = 3
}
