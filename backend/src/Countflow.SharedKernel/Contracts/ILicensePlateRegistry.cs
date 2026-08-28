using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>What another module is allowed to know about a handling unit.</summary>
/// <param name="HasStock">
/// Whether anything is posted against it right now. False for a plate that has
/// been labelled and not yet filled, and false again once it has been emptied —
/// the two are indistinguishable here on purpose, because the difference is a
/// question about history and <c>IStockHistory</c> is where history is answered.
/// </param>
public sealed record LicensePlateSnapshot(Guid Id, string Sscc, bool IsClosed, bool HasStock);

/// <summary>
/// Inventory's published surface for the existence of a licence plate, as opposed
/// to what is on one.
/// <para>
/// Until labels, a plate came into being as a side effect of posting stock against
/// an SSCC somebody scanned off a supplier's label — <c>StockReferenceResolver</c>
/// created it on first sight and nothing else ever needed to. Printing inverts
/// that: the label is made <i>before</i> the pallet is built, and the moment a
/// physical label exists is the moment the plate has to be real, because from then
/// on there is an object in the building carrying that number.
/// </para>
/// <para>
/// <see cref="EnsureAsync"/> is therefore deliberately not called <c>CreateAsync</c>.
/// A reprint must land on the plate the first print made rather than a second one,
/// and a plate that already holds stock must be returned untouched rather than
/// refused — reprinting the label for a pallet that is already built is the single
/// most common reason anybody prints a pallet label twice.
/// </para>
/// <para>
/// There is no delete here, and there will not be one. A plate is referenced by
/// ledger movements, so removing it would leave an append-only record pointing at
/// a container that no longer exists (ADR 0001).
/// </para>
/// </summary>
public interface ILicensePlateRegistry
{
    /// <summary>
    /// The plate for an SSCC, created if this is the first thing to claim it.
    /// Validates the check digit either way — see <c>Sscc</c> in SharedKernel for
    /// why a mis-read licence plate is worse than a rejected one.
    /// </summary>
    Task<Result<LicensePlateSnapshot>> EnsureAsync(string sscc, CancellationToken cancellationToken);

    /// <summary>The plate for an SSCC, or null. Creates nothing.</summary>
    Task<LicensePlateSnapshot?> FindAsync(string sscc, CancellationToken cancellationToken);
}
