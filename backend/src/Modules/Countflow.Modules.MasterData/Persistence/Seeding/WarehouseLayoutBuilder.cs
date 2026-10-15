using Countflow.Modules.MasterData.Domain;
using Countflow.SharedKernel.Routing;

namespace Countflow.Modules.MasterData.Persistence.Seeding;

/// <summary>
/// Builds a believable distribution-centre layout.
/// <para>
/// Deliberately not three tidy bins: rule evaluation, pick routing and slotting
/// only behave interestingly at realistic scale, and a demo warehouse with a
/// dozen locations would hide every problem worth finding. Shapes here match
/// what a mid-sized ambient-and-chilled DC actually looks like.
/// </para>
/// </summary>
public sealed class WarehouseLayoutBuilder
{
    public sealed record Layout(
        Warehouse Warehouse,
        List<Area> Areas,
        List<Zone> Zones,
        List<Location> Locations);

    public Layout Build()
    {
        var warehouse = new Warehouse
        {
            Code = "WH1",
            Name = "Rotterdam Distribution Centre",
            // The same fictional prefix ItemCatalogueBuilder puts on its GTINs —
            // not allocated to anyone real. Without one the seeded site cannot
            // mint an SSCC, and a pallet label at a blind receipt is the first
            // thing anybody tries. See ISsccMint.
            Gs1CompanyPrefix = "509876",
            City = "Rotterdam",
            CountryCode = "NL"
        };

        var areas = new List<Area>();
        var zones = new List<Zone>();
        var locations = new List<Location>();

        Area AddArea(string code, string name)
        {
            var area = new Area { WarehouseId = warehouse.Id, Code = code, Name = name };
            areas.Add(area);
            return area;
        }

        Zone AddZone(
            Area area,
            string code,
            string name,
            TemperatureClass temperature,
            int pickSequence,
            string routeStrategy = PickRouteCatalogue.SequenceOrdinal,
            bool hasRearCrossAisle = false)
        {
            var zone = new Zone
            {
                AreaId = area.Id,
                Code = code,
                Name = name,
                TemperatureClass = temperature,
                PickSequence = pickSequence,
                PickRouteStrategy = routeStrategy,
                HasRearCrossAisle = hasRearCrossAisle
            };

            zones.Add(zone);
            return zone;
        }

        var receiving = AddArea("RECV", "Receiving");
        var storage = AddArea("STOR", "Storage");
        var packing = AddArea("PACK", "Packing");
        var shipping = AddArea("SHIP", "Shipping");
        var returns = AddArea("RTN", "Returns");

        var inboundDock = AddZone(receiving, "DOCK", "Inbound docks", TemperatureClass.Ambient, 0);

        // The two halls are seeded with different strategies on purpose, and not to
        // show off. A demo where every zone walks the same way cannot demonstrate the
        // one property the design turns on — that the strategy is chosen per zone
        // because which one wins depends on the shape of the rack and the density of
        // the picks, which is a thing a supervisor knows and the system cannot infer.
        //
        // Ambient is twenty-four aisles of ten bays with a cross-aisle at each end,
        // which is the textbook S-Shape hall. Chilled is six short aisles off a single
        // gangway; traversing one of those to the far wall for a single carton is the
        // failure Return exists to avoid, and marking it as having a rear cross-aisle
        // it does not have would send every picker in it into a dead end.
        var ambient = AddZone(
            storage, "AMB", "Ambient storage", TemperatureClass.Ambient, 10,
            PickRouteCatalogue.SShape, hasRearCrossAisle: true);

        var chilled = AddZone(
            storage, "CHL", "Chilled storage", TemperatureClass.Chilled, 20,
            PickRouteCatalogue.Return);

        var packZone = AddZone(packing, "PACK", "Pack benches", TemperatureClass.Ambient, 90);
        var outboundDock = AddZone(shipping, "DOCK", "Outbound docks", TemperatureClass.Ambient, 99);
        var quarantine = AddZone(returns, "QUAR", "Quarantine", TemperatureClass.Ambient, 100);

        // ---- Dock doors ----
        for (var door = 1; door <= 8; door++)
        {
            locations.Add(Dock(warehouse, inboundDock, receiving, "DOCK", door, LocationType.ReceivingDock));
        }

        for (var door = 1; door <= 8; door++)
        {
            locations.Add(Dock(warehouse, outboundDock, shipping, "DOCK", door, LocationType.ShippingDock));
        }

        // ---- Racking ----
        //
        // Level 01 is the pick face: reachable without a truck, so that is where
        // pickers are sent and what replenishment tops up. Levels above it are
        // reserve, held on pallets and not pickable.
        locations.AddRange(BuildRacking(
            warehouse, storage, ambient,
            aisleCount: 24, baysPerAisle: 10, levelsPerBay: 4));

        locations.AddRange(BuildRacking(
            warehouse, storage, chilled,
            aisleCount: 6, baysPerAisle: 8, levelsPerBay: 3));

        // ---- Pack benches ----
        for (var bench = 1; bench <= 6; bench++)
        {
            locations.Add(new Location
            {
                WarehouseId = warehouse.Id,
                ZoneId = packZone.Id,
                Code = LocationCode.ForBin("WH1", "PACK", "PACK", "BENCH", "00", "00", $"{bench:D2}"),
                Type = LocationType.Packing,
                Position = $"{bench:D2}",
                PickSequence = bench,
                IsPickable = false,
                IsReplenishable = false
            });
        }

        // ---- Quarantine ----
        for (var bay = 1; bay <= 10; bay++)
        {
            locations.Add(new Location
            {
                WarehouseId = warehouse.Id,
                ZoneId = quarantine.Id,
                Code = LocationCode.ForBin("WH1", "RTN", "QUAR", "Q01", $"{bay:D2}", "01", "A"),
                Type = LocationType.Quarantine,
                Aisle = "Q01",
                Bay = $"{bay:D2}",
                Level = "01",
                Position = "A",
                PickSequence = bay,
                MaxVolumeM3 = 2.0m,
                MaxWeightKg = 800m,
                IsPickable = false,
                IsReplenishable = false
            });
        }

        // ---- Virtual counterparties ----
        //
        // These make the Phase 2 double-entry ledger work with one mechanism:
        // stock is never created, it moves in from Supplier and out to Customer.
        foreach (var type in new[]
                 {
                     LocationType.Supplier,
                     LocationType.Customer,
                     LocationType.Adjustment,
                     LocationType.Scrap,
                     LocationType.InTransit
                 })
        {
            locations.Add(new Location
            {
                WarehouseId = warehouse.Id,
                ZoneId = null,
                Code = LocationCode.ForVirtual("WH1", type),
                Type = type,
                IsPickable = false,
                IsReplenishable = false
            });
        }

        return new Layout(warehouse, areas, zones, locations);
    }

    private static Location Dock(
        Warehouse warehouse,
        Zone zone,
        Area area,
        string zoneCode,
        int door,
        LocationType type) =>
        new()
        {
            WarehouseId = warehouse.Id,
            ZoneId = zone.Id,
            Code = LocationCode.ForBin("WH1", area.Code, zoneCode, "DOOR", "00", "00", $"D{door:D2}"),
            Type = type,
            Position = $"D{door:D2}",
            PickSequence = door,
            IsPickable = false,
            IsReplenishable = false
        };

    private static List<Location> BuildRacking(
        Warehouse warehouse,
        Area area,
        Zone zone,
        int aisleCount,
        int baysPerAisle,
        int levelsPerBay)
    {
        var locations = new List<Location>(aisleCount * baysPerAisle * levelsPerBay * 2);

        for (var aisle = 1; aisle <= aisleCount; aisle++)
        {
            var aisleCode = $"A{aisle:D2}";

            for (var bay = 1; bay <= baysPerAisle; bay++)
            {
                for (var level = 1; level <= levelsPerBay; level++)
                {
                    foreach (var position in (ReadOnlySpan<string>)["A", "B"])
                    {
                        var isPickFace = level == 1;

                        locations.Add(new Location
                        {
                            WarehouseId = warehouse.Id,
                            ZoneId = zone.Id,
                            Code = LocationCode.ForBin(
                                "WH1", area.Code, zone.Code, aisleCode, $"{bay:D2}", $"{level:D2}", position),
                            Type = isPickFace ? LocationType.PickFace : LocationType.Bulk,
                            Aisle = aisleCode,
                            Bay = $"{bay:D2}",
                            Level = $"{level:D2}",
                            Position = position,

                            // Serpentine ordering: even aisles run backwards, so a
                            // picker walks down one aisle and up the next instead
                            // of returning to the start each time.
                            PickSequence = aisle % 2 == 0
                                ? (aisle * 10_000) + ((baysPerAisle - bay) * 100) + level
                                : (aisle * 10_000) + (bay * 100) + level,

                            // A pick face is a shelf; reserve above it is a pallet slot.
                            MaxVolumeM3 = isPickFace ? 0.6m : 1.8m,
                            MaxWeightKg = isPickFace ? 250m : 1_000m,
                            MaxDistinctItems = isPickFace ? 1 : null,

                            IsPickable = isPickFace,
                            IsReplenishable = isPickFace
                        });
                    }
                }
            }
        }

        return locations;
    }
}
