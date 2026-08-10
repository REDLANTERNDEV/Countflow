using Countflow.Modules.MasterData.Domain;
using Countflow.SharedKernel.Gs1;

namespace Countflow.Modules.MasterData.Persistence.Seeding;

/// <summary>
/// Generates a catalogue with the shape a real one has: a long tail of slow
/// movers, a handful of fast ones, mixed tracking policies, and pack hierarchies
/// whose GTINs carry correct check digits.
/// <para>
/// The distributions matter. ABC drives slotting and cycle-count frequency;
/// lot and expiry tracking decide whether allocation is FIFO or FEFO. A
/// catalogue where every item is identical would let broken rules look correct.
/// </para>
/// </summary>
public sealed class ItemCatalogueBuilder(int itemCount = 500, int randomSeed = 20260810)
{
    // Fixed seed: the demo, screenshots and any future shadow-replay baseline
    // must be reproducible.
    private readonly Random _random = new(randomSeed);

    private static readonly string[] Categories =
    [
        "Olive Oil", "Pasta", "Rice", "Coffee Beans", "Tea", "Flour", "Sugar", "Tinned Tomatoes",
        "Chickpeas", "Sea Salt", "Black Pepper", "Paprika", "Soap Bar", "Shampoo", "Detergent",
        "Kitchen Roll", "Bin Liners", "Batteries", "Light Bulb", "Sponge Pack", "Cling Film",
        "Aluminium Foil", "Dish Soap", "Hand Cream", "Toothpaste", "Cereal", "Biscuits",
        "Crackers", "Honey", "Jam", "Peanut Butter", "Vinegar", "Mustard", "Ketchup"
    ];

    private static readonly string[] ChilledCategories =
    [
        "Fresh Milk", "Butter", "Yoghurt", "Cheddar", "Mozzarella", "Cream", "Fresh Juice",
        "Sliced Ham", "Chicken Breast", "Smoked Salmon", "Fresh Pasta", "Hummus"
    ];

    private static readonly string[] Descriptors =
    [
        "Organic", "Extra Virgin", "Wholegrain", "Reduced Salt", "Premium", "Value",
        "Family Pack", "Single Origin", "Free Range", "Gluten Free", "Classic", "Traditional"
    ];

    private static readonly string[] Sizes = ["250g", "500g", "1kg", "2kg", "330ml", "500ml", "1L", "2L", "6pk", "12pk"];

    /// <summary>A fictional GS1 company prefix — not allocated to anyone real.</summary>
    private const string CompanyPrefix = "509876";

    public List<Item> Build()
    {
        var items = new List<Item>(itemCount);
        var serialReference = 1000;

        for (var index = 0; index < itemCount; index++)
        {
            var isChilled = _random.NextDouble() < 0.18;

            var category = isChilled
                ? ChilledCategories[_random.Next(ChilledCategories.Length)]
                : Categories[_random.Next(Categories.Length)];

            var descriptor = Descriptors[_random.Next(Descriptors.Length)];
            var size = Sizes[_random.Next(Sizes.Length)];

            // Chilled goods are perishable and effectively always lot-and-expiry
            // tracked; ambient dry goods often are not. That asymmetry is what
            // makes FEFO worth implementing.
            var isLotTracked = isChilled || _random.NextDouble() < 0.35;
            var isExpiryTracked = isChilled || (isLotTracked && _random.NextDouble() < 0.7);
            var isSerialTracked = !isChilled && _random.NextDouble() < 0.03;

            var abc = _random.NextDouble() switch
            {
                < 0.20 => AbcClass.A,
                < 0.50 => AbcClass.B,
                _ => AbcClass.C
            };

            var item = new Item
            {
                Sku = $"SKU-{10_000 + index}",
                Description = $"{descriptor} {category} {size}",
                BaseUom = "EA",
                AbcClass = abc,
                TemperatureClass = isChilled ? TemperatureClass.Chilled : TemperatureClass.Ambient,
                IsLotTracked = isLotTracked,
                IsSerialTracked = isSerialTracked,
                IsExpiryTracked = isExpiryTracked,
                ShelfLifeDays = isExpiryTracked ? (isChilled ? _random.Next(7, 45) : _random.Next(120, 900)) : null,
                MinimumRemainingShelfLifePercent = isExpiryTracked ? (isChilled ? 50 : 33) : null,
                IsHazardous = !isChilled && _random.NextDouble() < 0.02
            };

            foreach (var pack in BuildPackHierarchy(item, ref serialReference))
            {
                item.PackConfigurations.Add(pack);
            }

            items.Add(item);
        }

        return items;
    }

    private List<PackConfiguration> BuildPackHierarchy(Item item, ref int serialReference)
    {
        var innerQty = _random.Next(0, 3) switch { 0 => 0, 1 => 6, _ => 12 };
        var casesPerLayer = _random.Next(4, 9);
        var layersPerPallet = _random.Next(4, 9);

        var eachWeight = Math.Round((decimal)(_random.NextDouble() * 1.8 + 0.15), 3);
        var eachLength = _random.Next(50, 160);
        var eachWidth = _random.Next(40, 120);
        var eachHeight = _random.Next(60, 300);

        var packs = new List<PackConfiguration>();

        packs.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Each,
            BaseUnitQuantity = 1,
            Gtin = NextGtin(ref serialReference),
            LengthMm = eachLength,
            WidthMm = eachWidth,
            HeightMm = eachHeight,
            GrossWeightKg = eachWeight,
            IsPickUnit = true
        });

        var caseQty = innerQty > 0 ? innerQty * _random.Next(2, 5) : _random.Next(6, 25);

        if (innerQty > 0)
        {
            packs.Add(new PackConfiguration
            {
                ItemId = item.Id,
                Level = PackagingLevel.Inner,
                BaseUnitQuantity = innerQty,
                // Plenty of real items carry no barcode on the inner pack.
                Gtin = _random.NextDouble() < 0.6 ? NextGtin(ref serialReference) : null,
                LengthMm = eachLength * 2,
                WidthMm = eachWidth * (innerQty / 2m),
                HeightMm = eachHeight,
                GrossWeightKg = Math.Round(eachWeight * innerQty * 1.05m, 3)
            });
        }

        packs.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Case,
            BaseUnitQuantity = caseQty,
            Gtin = NextGtin(ref serialReference),
            LengthMm = 400,
            WidthMm = 300,
            HeightMm = Math.Min(eachHeight + 20, 400),
            GrossWeightKg = Math.Round(eachWeight * caseQty * 1.08m, 3),
            IsReceivingUnit = true
        });

        packs.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Pallet,
            BaseUnitQuantity = caseQty * casesPerLayer * layersPerPallet,
            Gtin = NextGtin(ref serialReference),
            LengthMm = 1200,
            WidthMm = 800,
            HeightMm = 1200,
            GrossWeightKg = Math.Round((eachWeight * caseQty * casesPerLayer * layersPerPallet * 1.1m) + 25m, 3)
        });

        return packs;
    }

    /// <summary>
    /// Builds a GTIN-14 with a correct check digit, so seeded barcodes survive
    /// the same validation a scanned one goes through. Fake barcodes that fail
    /// their own check digit make the demo lie about whether scanning works.
    /// </summary>
    private static string NextGtin(ref int serialReference)
    {
        var payload = $"0{CompanyPrefix}{serialReference:D6}";
        serialReference++;

        return payload + Gtin.CalculateCheckDigit(payload);
    }
}
