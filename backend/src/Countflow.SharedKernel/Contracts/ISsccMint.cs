using Countflow.SharedKernel.Results;

namespace Countflow.SharedKernel.Contracts;

/// <summary>
/// Issues Serial Shipping Container Codes on this company's own GS1 prefix.
/// <para>
/// <b>This is the one contract in Countflow whose output leaves the building and
/// never comes back.</b> Everything else the system writes can be corrected by a
/// compensating entry; an SSCC goes out of the door stuck to a pallet, gets read
/// by a haulier, a customer and possibly a customs system, and there is no posting
/// that unprints it. So the rule here is stricter than anywhere else in the
/// codebase: <b>a serial reference is allocated once and is never reissued</b>,
/// not after a rollback, not after a failed print, not after a restore from
/// backup. The cost of burning a serial nobody used is nothing. The cost of two
/// pallets carrying one SSCC is a customer receiving goods against a licence plate
/// that already means something else.
/// </para>
/// <para>
/// That is why the serial comes from a database sequence rather than from a
/// <c>MAX(serial) + 1</c> over a table. A sequence does not roll back with the
/// transaction that drew from it, which is normally a nuisance and here is the
/// entire point.
/// </para>
/// <para>
/// Lives with MasterData because the GS1 company prefix is an attribute of the
/// company operating the site — reference data somebody is given by GS1 and types
/// in once — and because minting is arithmetic over that prefix and nothing else.
/// Inventory is not involved: a minted SSCC is a <i>name</i>, and it becomes a
/// licence plate only when something claims it (see
/// <see cref="ILicensePlateRegistry"/>).
/// </para>
/// </summary>
public interface ISsccMint
{
    /// <summary>
    /// Allocates the next SSCC for a warehouse, check digit already applied.
    /// <para>
    /// Fails rather than improvises when the warehouse has no GS1 company prefix
    /// configured. A made-up prefix produces a structurally valid eighteen digits
    /// that belong to somebody else's company, which is worse than no label at
    /// all: it is a label that scans, resolves, and is wrong everywhere outside
    /// this warehouse.
    /// </para>
    /// </summary>
    Task<Result<string>> MintAsync(Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a warehouse could mint. Read by the label editor so a supervisor
    /// laying out a pallet template learns the prefix is missing at the moment
    /// they are configuring the site, rather than at a dock at six in the morning.
    /// </summary>
    Task<bool> CanMintAsync(Guid warehouseId, CancellationToken cancellationToken);
}
