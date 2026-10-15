using Countflow.SharedKernel.Modules;

namespace Countflow.SharedKernel.Contracts;

/// <summary>Scoped, module-owned discovery of configured instances of a code handler.</summary>
public interface IBackgroundJobCatalog
{
    Task<IReadOnlyList<BackgroundJobRegistration>> ListAsync(CancellationToken cancellationToken);
}
