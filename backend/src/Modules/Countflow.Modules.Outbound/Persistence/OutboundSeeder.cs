using Countflow.Modules.Outbound.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Outbound.Persistence;

/// <summary>
/// A few orders waiting to go out, so the outbound screens have something to
/// release on a fresh install.
/// <para>
/// Demo data, and gated as such — there is no reference data an outbound loop
/// cannot run without, unlike the rule sets. Deliberately left in
/// <see cref="SalesOrderStatus.New"/>: releasing them would commit stock that has
/// not been received yet, and an order allocated against an empty warehouse is a
/// worse first impression than an order waiting for one.
/// </para>
/// </summary>
public sealed class OutboundSeeder(
    OutboundDbContext dbContext,
    IItemDirectory items,
    ILocationDirectory locations,
    IWarehouseDirectory warehouses,
    IConfiguration configuration,
    ILogger<OutboundSeeder> logger) : IModuleSeeder
{
    /// <summary>After master data, whose items, benches and warehouses the orders reference.</summary>
    public int Order => 40;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Seed:DemoData", false))
        {
            return;
        }

        if (await dbContext.SalesOrders.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Sales orders already present; skipping seed");
            return;
        }

        var warehouse = (await warehouses.ListAsync(cancellationToken)).FirstOrDefault();
        if (warehouse is null)
        {
            logger.LogWarning("No warehouse to seed orders against; skipping");
            return;
        }

        // The same SKUs the advice notes announce, so a demo can receive a
        // delivery and then ship it — which is the whole round trip, and the only
        // sequence worth showing somebody.
        var skus = await ResolveAsync(cancellationToken);
        if (skus.Count < 4)
        {
            logger.LogWarning("Too few seeded items to build orders; skipping");
            return;
        }

        var bench = await FindBenchAsync(warehouse.Id, cancellationToken);
        if (bench is null)
        {
            logger.LogWarning("No pack bench to stage orders at; skipping");
            return;
        }

        var shipBy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        dbContext.SalesOrders.AddRange(
            Draft(warehouse.Id, "SO-2026-1041", "Kruidvat Nederland BV", bench.Value, shipBy, skus[0], 120, skus[1], 48),
            Draft(warehouse.Id, "SO-2026-1042", "Delhaize België NV", bench.Value, shipBy, skus[2], 300, skus[3], 24),
            Draft(warehouse.Id, "SO-2026-1043", "Migros Ticaret AŞ", bench.Value, shipBy.AddDays(1), skus[0], 60, skus[2], 150));

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded 3 sales orders");
    }

    private async Task<IReadOnlyList<ItemSnapshot>> ResolveAsync(CancellationToken cancellationToken)
    {
        var found = new List<ItemSnapshot>();

        for (var index = 1; index <= 24 && found.Count < 4; index++)
        {
            if (await items.FindBySkuAsync($"SKU-{10_000 + index}", cancellationToken) is { IsActive: true } item)
            {
                found.Add(item);
            }
        }

        return found;
    }

    /// <summary>
    /// The first pack bench, resolved through the published directory rather than
    /// hard-coded. The layout seeder builds six of them and their codes are its
    /// business, not this module's.
    /// </summary>
    private async Task<Guid?> FindBenchAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var candidates = await locations.FindStorageCandidatesAsync(warehouseId, cancellationToken);

        return candidates
            .Where(location => location.Role == StorageRole.Bench && location.IsActive)
            .OrderBy(location => location.Code, StringComparer.Ordinal)
            .Select(location => (Guid?)location.Id)
            .FirstOrDefault();
    }

    private static SalesOrder Draft(
        Guid warehouseId,
        string reference,
        string customer,
        Guid stagingLocationId,
        DateOnly shipBy,
        ItemSnapshot first,
        decimal firstQuantity,
        ItemSnapshot second,
        decimal secondQuantity)
    {
        var order = new SalesOrder
        {
            WarehouseId = warehouseId,
            Reference = reference,
            CustomerName = customer,
            StagingLocationId = stagingLocationId,
            ShipBy = shipBy
        };

        order.Lines.Add(Line(order, 1, first, firstQuantity));
        order.Lines.Add(Line(order, 2, second, secondQuantity));

        return order;
    }

    private static SalesOrderLine Line(SalesOrder order, int number, ItemSnapshot item, decimal quantity) => new()
    {
        SalesOrderId = order.Id,
        LineNumber = number,
        ItemId = item.Id,
        OrderedQuantity = quantity
    };
}
