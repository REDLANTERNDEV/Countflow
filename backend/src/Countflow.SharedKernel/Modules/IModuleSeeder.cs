namespace Countflow.SharedKernel.Modules;

/// <summary>
/// Seeds data a module cannot function without (roles, reference data), plus
/// demo data when enabled. Runs in the migrator after migrations, never on API
/// startup. Implementations must be idempotent — they run on every deploy.
/// </summary>
public interface IModuleSeeder
{
    /// <summary>Lower runs first. Use it where one module's seed depends on another's.</summary>
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
