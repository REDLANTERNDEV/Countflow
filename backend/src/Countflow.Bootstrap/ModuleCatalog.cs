using Countflow.Modules.Identity;
using Countflow.Modules.Inbound;
using Countflow.Modules.Inventory;
using Countflow.Modules.MasterData;
using Countflow.Modules.Rules;
using Countflow.Modules.Work;
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
        new InventoryModule(),
        // Phase 3. Rules reads items, locations and stock and decides; Work turns
        // a decision into a job and a confirmed job into a ledger entry; Inbound
        // orchestrates the three. None of them can name another's DbContext.
        new RulesModule(),
        new WorkModule(),
        new InboundModule()
    ];
}
