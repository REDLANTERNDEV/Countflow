using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Countflow.SharedKernel.Modules;

/// <summary>
/// Readiness for every module schema at once.
/// <para>
/// Written this way to keep a promise the rest of the design makes: adding a
/// module is one line in <c>ModuleCatalog</c>. A per-context
/// <c>AddDbContextCheck&lt;T&gt;</c> line in the host would be a second place to
/// remember, and the failure mode is silent — the module serves traffic and
/// readiness simply stops covering it, which is exactly when you want it to.
/// </para>
/// <para>
/// Each context is probed separately and named in the result, so a report says
/// which schema is unreachable rather than only that something is.
/// </para>
/// </summary>
public sealed class ModuleHealthCheck(IEnumerable<IModuleDbContext> contexts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var unreachable = new List<string>();

        foreach (var moduleContext in contexts)
        {
            var name = moduleContext.GetType().Name;

            try
            {
                if (!await moduleContext.Database.CanConnectAsync(cancellationToken))
                {
                    unreachable.Add(name);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unreachable.Add($"{name} ({exception.GetType().Name})");
            }
        }

        return unreachable.Count == 0
            ? HealthCheckResult.Healthy($"All {contexts.Count()} module schemas reachable")
            : HealthCheckResult.Unhealthy($"Unreachable module schemas: {string.Join(", ", unreachable)}");
    }
}
