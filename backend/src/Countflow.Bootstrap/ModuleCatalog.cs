using Countflow.Modules.Identity;
using Countflow.Modules.Inventory;
using Countflow.Modules.MasterData;
using Countflow.SharedKernel.Modules;

namespace Countflow.Bootstrap;

/// <summary>
/// The single list of modules that make up Countflow.
/// <para>
/// Both the API host and the migrator compose from this, so a module can never
/// be served but unmigrated (or vice versa). Adding a module means adding one
/// line here.
/// </para>
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ICountflowModule> All { get; } =
    [
        new IdentityModule(),
        new MasterDataModule(),
        // Order matters only for seeding, which IModuleSeeder.Order controls, but
        // reading the list top to bottom as a dependency order is a useful habit:
        // Inventory posts against MasterData's locations, never the reverse.
        new InventoryModule()
    ];
}
