using Countflow.Modules.Inbound.Domain;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Inbound.Persistence;

/// <summary>
/// A few trucks on their way in, so the dock screen has something to receive
/// against on a fresh install.
/// <para>
/// Demo data, and gated as such — unlike the rule set, which the system cannot
/// run without. Items are picked from whatever the catalogue seeder produced
/// rather than named, so this keeps working when the item count is turned up or
/// down.
/// </para>
/// </summary>
public sealed class InboundSeeder(
    InboundDbContext dbContext,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IConfiguration configuration,
    ILogger<InboundSeeder> logger) : IModuleSeeder
{
    /// <summary>After master data, whose items and warehouses the notes reference.</summary>
    public int Order => 30;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Seed:DemoData", false))
        {
            return;
        }

        if (await dbContext.Asns.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Advice notes already present; skipping seed");
            return;
        }

        var warehouse = (await warehouses.ListAsync(cancellationToken)).FirstOrDefault();
        if (warehouse is null)
        {
            logger.LogWarning("No warehouse to seed advice notes against; skipping");
            return;
        }

        // Demo data referring to demo data. The item catalogue seeder numbers its
        // SKUs from SKU-10001, and this depends on that — an honest coupling
        // between two things that only exist together. What it does not do is
        // *assume* it: every SKU is resolved through the published directory, so
        // a change to the numbering leaves this skipping with a warning rather
        // than writing advice notes whose lines reference nothing.
        var skus = await ResolveAsync(cancellationToken);
        if (skus.Count < 6)
        {
            logger.LogWarning("Too few seeded items to build advice notes; skipping");
            return;
        }

        var expected = DateTimeOffset.UtcNow.AddHours(4);

        dbContext.Asns.AddRange(
            Note(warehouse.Id, "ASN-2026-0481", "Nordkap Foods AS", expected, skus[0], 480, skus[1], 240),
            Note(warehouse.Id, "ASN-2026-0482", "Meridian Trading BV", expected.AddHours(3), skus[2], 1_200, skus[3], 60),
            Note(warehouse.Id, "ASN-2026-0483", "Larssen Kjøl AS", expected.AddDays(1), skus[4], 96, skus[5], 720));

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded 3 advice notes");
    }

    private async Task<IReadOnlyList<ItemSnapshot>> ResolveAsync(CancellationToken cancellationToken)
    {
        var found = new List<ItemSnapshot>();

        for (var index = 1; index <= 24 && found.Count < 6; index++)
        {
            if (await items.FindBySkuAsync($"SKU-{10_000 + index}", cancellationToken) is { IsActive: true } item)
            {
                found.Add(item);
            }
        }

        return found;
    }

    private static Asn Note(
        Guid warehouseId,
        string reference,
        string supplier,
        DateTimeOffset expectedAt,
        ItemSnapshot first,
        decimal firstQuantity,
        ItemSnapshot second,
        decimal secondQuantity)
    {
        var asn = new Asn
        {
            WarehouseId = warehouseId,
            Reference = reference,
            SupplierName = supplier,
            ExpectedAt = expectedAt,
            CarrierReference = $"TRL-{reference[^4..]}"
        };

        asn.Lines.Add(Line(asn, 1, first, firstQuantity));
        asn.Lines.Add(Line(asn, 2, second, secondQuantity));

        return asn;
    }

    private static AsnLine Line(Asn asn, int number, ItemSnapshot item, decimal quantity) => new()
    {
        AsnId = asn.Id,
        LineNumber = number,
        ItemId = item.Id,
        ExpectedQuantity = quantity,

        // A lot-tracked item announced without a batch would fail at the dock, and
        // failing on the demo data is not the lesson this seed is here to teach.
        ExpectedLotNumber = item.IsLotTracked ? $"L{DateTime.UtcNow:yyMM}{number:D2}" : null,
        ExpectedExpiryDate = item.IsExpiryTracked
            ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(180))
            : null
    };
}
