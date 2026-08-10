using System.Diagnostics;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.MasterData.Persistence;

public sealed class MasterDataSeeder(
    MasterDataDbContext dbContext,
    IConfiguration configuration,
    ILogger<MasterDataSeeder> logger) : IModuleSeeder
{
    public int Order => 10;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Seed:DemoData", false))
        {
            return;
        }

        if (await dbContext.Warehouses.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Master data already present; skipping seed");
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        var layout = new WarehouseLayoutBuilder().Build();

        dbContext.Warehouses.Add(layout.Warehouse);
        dbContext.Areas.AddRange(layout.Areas);
        dbContext.Zones.AddRange(layout.Zones);
        dbContext.Locations.AddRange(layout.Locations);

        var itemCount = configuration.GetValue("Seed:ItemCount", 500);
        var items = new ItemCatalogueBuilder(itemCount).Build();
        dbContext.Items.AddRange(items);

        var partners = BuildPartners();
        dbContext.BusinessPartners.AddRange(partners);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded {Areas} areas, {Zones} zones, {Locations} locations, {Items} items ({Packs} pack levels), {Partners} partners in {Elapsed}ms",
            layout.Areas.Count,
            layout.Zones.Count,
            layout.Locations.Count,
            items.Count,
            items.Sum(i => i.PackConfigurations.Count),
            partners.Count,
            stopwatch.ElapsedMilliseconds);
    }

    private static List<BusinessPartner> BuildPartners()
    {
        (string Code, string Name, PartnerRole Roles, string City, string Country)[] partners =
        [
            ("SUP-ANDES", "Andes Coffee Traders", PartnerRole.Supplier, "Antwerp", "BE"),
            ("SUP-DELTA", "Delta Dry Goods", PartnerRole.Supplier, "Rotterdam", "NL"),
            ("SUP-NORTH", "Northfield Dairy", PartnerRole.Supplier, "Groningen", "NL"),
            ("SUP-IBERI", "Iberia Olive Co.", PartnerRole.Supplier, "Seville", "ES"),
            ("SUP-HOUSE", "Household Essentials BV", PartnerRole.Supplier | PartnerRole.Customer, "Utrecht", "NL"),
            ("CUS-METRO", "Metro Grocers", PartnerRole.Customer, "Amsterdam", "NL"),
            ("CUS-HARBR", "Harbour Retail Group", PartnerRole.Customer, "Rotterdam", "NL"),
            ("CUS-RHINE", "Rhine Convenience", PartnerRole.Customer, "Duisburg", "DE"),
            ("CUS-BRUGE", "Bruges Fine Foods", PartnerRole.Customer, "Bruges", "BE"),
            ("CAR-EURO", "EuroFreight Logistics", PartnerRole.Carrier, "Venlo", "NL"),
            ("CAR-SWIFT", "Swift Parcel", PartnerRole.Carrier, "Eindhoven", "NL")
        ];

        var reference = 100;

        return [.. partners.Select(partner =>
        {
            // GLNs are GS1 identifiers and take the same modulo-10 check digit,
            // so seeded ones stay valid against any EDI-side validation.
            var payload = $"509876{reference++:D6}";

            return new BusinessPartner
            {
                Code = partner.Code,
                Name = partner.Name,
                Roles = partner.Roles,
                Gln = payload + Gtin.CalculateCheckDigit(payload),
                City = partner.City,
                CountryCode = partner.Country
            };
        })];
    }
}
