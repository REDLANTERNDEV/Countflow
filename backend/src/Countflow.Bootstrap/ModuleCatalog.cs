using Countflow.Modules.Identity;
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
        new MasterDataModule()
    ];
}
