using Countflow.Modules.Counting;
using Countflow.Modules.Identity;
using Countflow.Modules.Inbound;
using Countflow.Modules.Inventory;
using Countflow.Modules.Labels;
using Countflow.Modules.MasterData;
using Countflow.Modules.Outbound;
using Countflow.Modules.Replay;
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
        new InboundModule(),
        // Phase 4. Outbound orchestrates the same three: Rules chooses which stock,
        // Inventory holds and moves it, Work fetches it. It publishes nothing and
        // names no other module's DbContext.
        new OutboundModule(),
        // Phase 5. The same three again, for the question the other two never ask:
        // not where stock should go or come from, but which of the numbers already
        // published are worth doubting.
        new CountingModule(),
        // Phase 6. The first module that asks nothing of the warehouse at all: it
        // reads decisions Rules has already made and history Inventory has already
        // written, and asks what a different rule set would have done with them.
        new ReplayModule(),
        // Printable identity. The last module in the list and the only one whose
        // output is a physical object: it reads items, plates and sites through
        // published contracts and emits ZPL. It publishes nothing, and the
        // transport that sends that ZPL to a printer is deliberately not here.
        new LabelsModule()
    ];
}
