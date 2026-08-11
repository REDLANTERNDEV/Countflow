namespace Countflow.Modules.Inventory.Domain;

/// <summary>
/// Why a movement happened, in the warehouse's own vocabulary.
/// <para>
/// Mandatory on every entry. A ledger that records a quantity change without a
/// reason can prove *that* stock moved but not *why*, and "why" is the only part
/// an auditor, a supplier claim, or a shrinkage investigation actually cares
/// about. Numbered in bands so a new reason can be added to a family without
/// renumbering the rest.
/// </para>
/// </summary>
public enum MovementReason
{
    // ---- Normal inbound and outbound flow ----

    PurchaseOrderReceipt = 1,
    CustomerReturn = 2,
    ProductionOutput = 3,

    PutawayComplete = 10,
    ReplenishmentPull = 11,
    ConsolidationMove = 12,
    LocationReorganisation = 13,

    OrderPick = 20,
    CustomerDespatch = 21,
    SupplierReturn = 22,

    // ---- Counting ----

    CycleCountVariance = 30,
    StocktakeVariance = 31,

    // ---- Quality and holds ----

    DamageFound = 40,
    QualityHoldApplied = 41,
    QualityHoldReleased = 42,
    RecallHold = 43,
    ExpiryReached = 44,

    // ---- Losses ----

    WriteOffDamaged = 50,
    WriteOffExpired = 51,
    Destroyed = 52,

    // ---- Corrections ----

    /// <summary>A supervisor correcting a known data error, with a note explaining it.</summary>
    ManualCorrection = 60,

    /// <summary>The compensating entry that undoes an earlier movement.</summary>
    ReversalOfEntry = 61,

    // ---- Inter-warehouse ----

    TransferOut = 70,
    TransferIn = 71
}
