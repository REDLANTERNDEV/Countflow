using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Countflow.SharedKernel.Modules;

/// <summary>
/// Marker for a module's DbContext. Every module context is registered under
/// this interface as well as its own type, which is what lets the migrator
/// enumerate and migrate all of them without referencing each one by name.
/// </summary>
public interface IModuleDbContext
{
    /// <summary>Satisfied automatically by <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</summary>
    DatabaseFacade Database { get; }
}
