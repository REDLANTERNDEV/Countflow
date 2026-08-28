using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Inventory's published surface for whether a licence plate exists (ADR 0002).
/// <para>
/// The same create-on-first-sight rule <c>StockReferenceResolver</c> has always
/// applied at a dock, published so that printing a label can be the first sight.
/// Deliberately <b>not</b> shared code with the resolver: that one creates a plate
/// inside a posting, where the surrounding transaction decides whether it
/// survives, and this one creates it for a physical label that exists whether the
/// posting happens or not. Two callers, one rule, two lifetimes — and folding them
/// together would mean a printed label disappearing because a receipt was
/// abandoned.
/// </para>
/// </summary>
public sealed class LicensePlateRegistry(InventoryDbContext dbContext) : ILicensePlateRegistry
{
    public async Task<Result<LicensePlateSnapshot>> EnsureAsync(
        string sscc,
        CancellationToken cancellationToken)
    {
        if (!Sscc.TryParse(sscc, out var normalised))
        {
            return Result.Failure<LicensePlateSnapshot>(InventoryErrors.InvalidSscc);
        }

        var existing = await FindRowAsync(normalised, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var plate = new LicensePlate { Sscc = normalised };

        dbContext.LicensePlates.Add(plate);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two labels for one SSCC printed at the same instant. The unique index
            // arbitrates and the loser reads what the winner wrote, because both
            // callers wanted the same thing — a plate with this number — and both
            // now have it. Refusing the second would fail a reprint for no reason
            // an operator could act on.
            dbContext.ChangeTracker.Clear();

            var raced = await FindRowAsync(normalised, cancellationToken);

            if (raced is null) throw;

            return raced;
        }

        return new LicensePlateSnapshot(plate.Id, plate.Sscc, plate.IsClosed, HasStock: false);
    }

    public async Task<LicensePlateSnapshot?> FindAsync(string sscc, CancellationToken cancellationToken) =>
        Sscc.TryParse(sscc, out var normalised)
            ? await FindRowAsync(normalised, cancellationToken)
            : null;

    private async Task<LicensePlateSnapshot?> FindRowAsync(string sscc, CancellationToken cancellationToken) =>
        await dbContext.LicensePlates
            .AsNoTracking()
            .Where(p => p.Sscc == sscc)
            .Select(p => new LicensePlateSnapshot(
                p.Id,
                p.Sscc,
                p.IsClosed,
                dbContext.StockBalances.Any(b => b.LicensePlateId == p.Id && b.Quantity > 0)))
            .SingleOrDefaultAsync(cancellationToken);
}
