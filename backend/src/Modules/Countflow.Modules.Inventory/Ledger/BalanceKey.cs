namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Identifies one row of the balance projection: a holding, plus the item and lot
/// occupying it.
/// <para>
/// Its own type, in its own file, because <see cref="LockOrderKey"/> is the one
/// definition that stops the module deadlocking against itself. Posting a
/// movement and reserving stock both take row locks on balances, and they are
/// different code paths in different classes — so the order they take them in has
/// to come from one place, or two writers eventually disagree about it and each
/// waits for the row the other is holding.
/// </para>
/// </summary>
internal readonly record struct BalanceKey(Guid ItemId, Guid? LotId, Holding Holding)
{
    /// <summary>
    /// The holding's key first, then the item and lot.
    /// <para>
    /// The extension is a <i>suffix</i> deliberately. A batch touches several
    /// balance rows at one location, so the holding alone no longer identifies a
    /// row — but a single-command posting, whose two rows share an item and lot,
    /// still sorts exactly as it did when the holding was the whole key. That is
    /// what lets a batch and a single command racing each other still agree on
    /// the order.
    /// </para>
    /// </summary>
    public string LockOrderKey => $"{Holding.LockOrderKey}|{ItemId:D}|{LotId:D}";
}
