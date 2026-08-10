namespace Countflow.Modules.MasterData.Domain;

/// <summary>
/// A rung on an item's packaging hierarchy.
/// <para>
/// This is why a GTIN belongs to a packaging level rather than to an item: a
/// bottle, the six-pack it ships in, and the pallet of six-packs each carry
/// their own distinct GTIN. Modelling one GTIN per item is the mistake that
/// makes case receiving impossible later.
/// </para>
/// </summary>
public enum PackagingLevel
{
    /// <summary>The base unit inventory is counted in.</summary>
    Each = 1,

    /// <summary>Intermediate pack — a sleeve, a six-pack.</summary>
    Inner = 2,

    /// <summary>Shipping carton. Usually the receiving and putaway unit.</summary>
    Case = 3,

    /// <summary>Full pallet quantity.</summary>
    Pallet = 4
}
