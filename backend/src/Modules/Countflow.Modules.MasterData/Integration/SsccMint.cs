using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.MasterData.Integration;

/// <summary>
/// Issues SSCCs on the warehouse's own GS1 company prefix (see
/// <see cref="ISsccMint"/> for why this is the most careful code in the module).
/// <para>
/// An SSCC is eighteen digits: one extension digit, then the company prefix, then
/// a serial reference filling whatever is left, then a modulo-10 check digit. The
/// prefix length is therefore what decides how many pallets a site can ever label
/// — a ten-digit prefix leaves six digits and a million serials, a six-digit
/// prefix leaves ten and effectively no limit. That arithmetic is done here rather
/// than assumed, and running out is a refusal rather than a wrap-around.
/// </para>
/// </summary>
public sealed class SsccMint(MasterDataDbContext dbContext) : ISsccMint
{
    /// <summary>
    /// Name of the sequence the serial reference is drawn from. A sequence and not
    /// a table, because <c>nextval</c> does not roll back with the transaction that
    /// called it — normally an inconvenience, and here the entire reason to use
    /// one. See <see cref="ISsccMint"/>.
    /// </summary>
    public const string SequenceName = "sscc_serial";

    private const int SsccDigits = 18;
    private const int CheckDigits = 1;
    private const int ExtensionDigits = 1;

    public async Task<Result<string>> MintAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var site = await dbContext.Warehouses
            .AsNoTracking()
            .Where(w => w.Id == warehouseId)
            .Select(w => new { w.Code, w.Gs1CompanyPrefix, w.SsccExtensionDigit })
            .SingleOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return Result.Failure<string>(MasterDataErrors.WarehouseNotFound);
        }

        if (string.IsNullOrWhiteSpace(site.Gs1CompanyPrefix))
        {
            return Result.Failure<string>(MasterDataErrors.NoCompanyPrefix(site.Code));
        }

        var prefix = site.Gs1CompanyPrefix.Trim();

        if (prefix.Length is < 6 or > 10 || !prefix.All(char.IsAsciiDigit))
        {
            return Result.Failure<string>(MasterDataErrors.InvalidCompanyPrefix(site.Code));
        }

        if (site.SsccExtensionDigit is < 0 or > 9)
        {
            return Result.Failure<string>(MasterDataErrors.InvalidExtensionDigit(site.Code));
        }

        var serialDigits = SsccDigits - CheckDigits - ExtensionDigits - prefix.Length;

        var serial = await NextSerialAsync(cancellationToken);

        // The serial has to fit the space the prefix left. Wrapping would reissue
        // a licence plate that is already on a pallet somewhere in the supply
        // chain, so this refuses and says so — a warehouse that reaches it needs a
        // second prefix from GS1, which is a purchase and not a code change.
        if (serial >= Pow10(serialDigits))
        {
            return Result.Failure<string>(MasterDataErrors.SsccSerialExhausted(site.Code));
        }

        var payload = $"{site.SsccExtensionDigit}{prefix}{serial.ToString().PadLeft(serialDigits, '0')}";

        return Sscc.FromPayload(payload);
    }

    public async Task<bool> CanMintAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var prefix = await dbContext.Warehouses
            .AsNoTracking()
            .Where(w => w.Id == warehouseId)
            .Select(w => w.Gs1CompanyPrefix)
            .SingleOrDefaultAsync(cancellationToken);

        return !string.IsNullOrWhiteSpace(prefix)
               && prefix.Trim().Length is >= 6 and <= 10
               && prefix.Trim().All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Draws the next serial. Raw SQL because EF has no expression for
    /// <c>nextval</c>, and because going through the change tracker is exactly the
    /// behaviour that must not happen here — a serial that participates in the
    /// caller's transaction is a serial that comes back after a rollback.
    /// </summary>
    private async Task<long> NextSerialAsync(CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT nextval('{MasterDataDbContext.SchemaName}.{SequenceName}')";

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(value);
    }

    private static long Pow10(int exponent)
    {
        var value = 1L;

        for (var index = 0; index < exponent; index++)
        {
            value *= 10;
        }

        return value;
    }
}
