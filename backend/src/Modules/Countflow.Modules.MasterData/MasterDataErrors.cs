using Countflow.SharedKernel.Results;

namespace Countflow.Modules.MasterData;

/// <summary>
/// MasterData's coded failures.
/// <para>
/// The module went five phases without one of these, because the reference data
/// it owned was only ever read or imported and the import endpoints carry their
/// own line-by-line reporting. Minting SSCCs is the first thing here that another
/// module calls and can be refused by, so the codes have to be stable and
/// documented like every other module's.
/// </para>
/// </summary>
public static class MasterDataErrors
{
    public static readonly Error WarehouseNotFound =
        Error.NotFound("masterdata.warehouse_not_found", "No warehouse with that identifier.");

    /// <summary>
    /// A site asked to mint a licence plate with no GS1 prefix on file. A
    /// configuration failure rather than a request failure, and it is stated as one
    /// — the caller cannot fix it by sending something different.
    /// </summary>
    public static Error NoCompanyPrefix(string warehouseCode) =>
        Error.Conflict(
            "masterdata.no_company_prefix",
            $"Warehouse {warehouseCode} has no GS1 company prefix, so it cannot issue SSCCs. Set one before printing pallet labels.");

    public static Error InvalidCompanyPrefix(string warehouseCode) =>
        Error.Conflict(
            "masterdata.invalid_company_prefix",
            $"Warehouse {warehouseCode}'s GS1 company prefix is not six to ten digits.");

    public static Error InvalidExtensionDigit(string warehouseCode) =>
        Error.Conflict(
            "masterdata.invalid_extension_digit",
            $"Warehouse {warehouseCode}'s SSCC extension digit is outside 0–9.");

    /// <summary>
    /// Every serial the prefix has room for has been issued. Refused rather than
    /// wrapped: see <c>ISsccMint</c> for why reissuing one is the worst thing this
    /// system could do.
    /// </summary>
    public static Error SsccSerialExhausted(string warehouseCode) =>
        Error.Conflict(
            "masterdata.sscc_serial_exhausted",
            $"Warehouse {warehouseCode} has issued every SSCC its GS1 prefix has room for. A further prefix has to be obtained from GS1.");
}
