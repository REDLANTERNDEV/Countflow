using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Ledger;

/// <summary>
/// Turns the identifiers people and integrations actually use — a SKU, a location
/// code, a lot number, an SSCC — into the keys the ledger posts against.
/// <para>
/// Commands take codes rather than GUIDs on purpose. A location code is printed on
/// the rack label and spoken on the phone; a supplier's EDI feed carries SKUs and
/// batch numbers. Making integrators look up surrogate keys first would add a
/// round trip and a class of mismatch bug for no benefit.
/// </para>
/// </summary>
public sealed class StockReferenceResolver(
    InventoryDbContext dbContext,
    IItemDirectory items,
    ILocationDirectory locations)
{
    public async Task<Result<ItemSnapshot>> ItemAsync(string? sku, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return Result.Failure<ItemSnapshot>(InventoryErrors.ItemNotFound);
        }

        var item = await items.FindBySkuAsync(sku.Trim(), cancellationToken);

        return item is null
            ? Result.Failure<ItemSnapshot>(InventoryErrors.ItemNotFound)
            : item;
    }

    public async Task<Result<LocationSnapshot>> LocationAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<LocationSnapshot>(InventoryErrors.LocationNotFound);
        }

        var location = await locations.FindByCodeAsync(code.Trim(), cancellationToken);

        return location is null
            ? Result.Failure<LocationSnapshot>(InventoryErrors.LocationNotFound)
            : location;
    }

    /// <summary>
    /// The virtual counterparty for a movement's other side. Its absence is a
    /// seeding failure, not user error: the ledger cannot record a receipt at all
    /// without a Supplier account to post it against.
    /// </summary>
    public async Task<Result<LocationSnapshot>> CounterpartyAsync(
        Guid warehouseId,
        CounterpartyKind kind,
        CancellationToken cancellationToken)
    {
        var location = await locations.FindCounterpartyAsync(warehouseId, kind, cancellationToken);

        return location is null
            ? Result.Failure<LocationSnapshot>(Error.Failure(
                "inventory.counterparty_missing",
                $"Warehouse {warehouseId} has no {kind} counterparty location. The ledger cannot post without it."))
            : location;
    }

    /// <summary>
    /// Resolves a batch, creating it on first sight when
    /// <paramref name="createIfMissing"/> is set.
    /// <para>
    /// Receiving creates lots; a transfer or a pick must not. A typo'd batch
    /// number at receipt costs a correction, but a typo at pick time would
    /// invent a batch that has never existed and quietly move stock into it.
    /// </para>
    /// <para>
    /// A lot created here survives a failed posting, because it is written before
    /// the ledger transaction opens. That is deliberate and harmless: a lot is a
    /// description of a batch, not a quantity, so an unused row holds no stock and
    /// the retry reuses it.
    /// </para>
    /// </summary>
    public async Task<Result<Guid?>> LotAsync(
        ItemSnapshot item,
        string? lotNumber,
        DateOnly? expiryDate,
        DateOnly? productionDate,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lotNumber))
        {
            // Whether a missing lot is acceptable is the item's policy, and the
            // ledger checks it against IsLotTracked. Not this resolver's call.
            return expiryDate is null
                ? Result.Success<Guid?>(null)
                : Result.Failure<Guid?>(InventoryErrors.ExpiryRequiresLot);
        }

        var trimmed = lotNumber.Trim();

        var existing = await dbContext.Lots
            .SingleOrDefaultAsync(l => l.ItemId == item.Id && l.LotNumber == trimmed, cancellationToken);

        if (existing is not null)
        {
            if (expiryDate is not null && existing.ExpiryDate is { } known && known != expiryDate)
            {
                // The same batch cannot expire on two dates. Accepting the second
                // one would silently change the rotation order of stock already on
                // the shelf; ignoring it would hide a real labelling problem.
                return Result.Failure<Guid?>(Error.Conflict(
                    "inventory.lot_expiry_conflict",
                    $"Lot {trimmed} is already recorded as expiring {known:yyyy-MM-dd}, not {expiryDate:yyyy-MM-dd}."));
            }

            existing.ExpiryDate ??= expiryDate;
            existing.ProductionDate ??= productionDate;

            await dbContext.SaveChangesAsync(cancellationToken);

            return existing.Id;
        }

        if (!createIfMissing)
        {
            return Result.Failure<Guid?>(InventoryErrors.LotNotFound);
        }

        var lot = new Lot
        {
            ItemId = item.Id,
            LotNumber = trimmed,
            ExpiryDate = expiryDate,
            ProductionDate = productionDate
        };

        dbContext.Lots.Add(lot);
        await dbContext.SaveChangesAsync(cancellationToken);

        return lot.Id;
    }

    /// <summary>
    /// Resolves a licence plate by SSCC, creating it on first sight when
    /// <paramref name="createIfMissing"/> is set. The check digit is validated
    /// either way — see <see cref="Sscc"/> for why a mis-read licence plate is
    /// worse than a rejected scan.
    /// </summary>
    public async Task<Result<Guid?>> LicensePlateAsync(
        string? sscc,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sscc))
        {
            return Result.Success<Guid?>(null);
        }

        if (!Sscc.TryParse(sscc, out var normalised))
        {
            return Result.Failure<Guid?>(InventoryErrors.InvalidSscc);
        }

        var existing = await dbContext.LicensePlates
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Sscc == normalised, cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        if (!createIfMissing)
        {
            return Result.Failure<Guid?>(InventoryErrors.LicensePlateNotFound);
        }

        var plate = new LicensePlate { Sscc = normalised };

        dbContext.LicensePlates.Add(plate);
        await dbContext.SaveChangesAsync(cancellationToken);

        return plate.Id;
    }
}
