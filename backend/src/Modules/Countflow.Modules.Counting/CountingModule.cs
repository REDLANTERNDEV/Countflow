using Countflow.Modules.Counting.Features.Plans;
using Countflow.Modules.Counting.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Counting;

/// <summary>
/// Cycle counting: which of the system's own numbers to go and check, and the work
/// that checks them.
/// <para>
/// An orchestrator, exactly as Inbound and Outbound are, and the third proof that
/// the shape holds. It owns count plans and nothing else — the holdings are chosen
/// by Rules, the jobs are carried by Work, and the findings and any corrections
/// belong to Inventory, each through a published contract. It writes no stock
/// figure of its own, which is the discipline every phase since the ledger has had
/// to keep.
/// </para>
/// <para>
/// Publishes nothing. Its output is work and, through that work, counts — and both
/// of those are already somebody else's surface.
/// </para>
/// <para>
/// The one thing it genuinely owns is the answer to "what is already on somebody's
/// list?", which is why the plan is a table rather than a report. Without it every
/// planning run would re-propose the same overdue bays the last one raised, and the
/// queue would fill with duplicates of exactly the holdings that matter most.
/// </para>
/// </summary>
public sealed class CountingModule : ICountflowModule
{
    public string Name => "Counting";

    public string Schema => CountingDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<CountingDbContext>(connectionString, Schema);

        services.AddScoped<CountPlanning>();
        services.AddScoped<CountPlanDescriber>();

        services.TryAddSingleton(TimeProvider.System);

        // No seeder. Counts are raised by policy against a warehouse that has stock
        // in it, and a seeded plan would be a set of jobs pointing at holdings the
        // demo data may not have created yet.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        CreateCountPlan.Map(endpoints);
        GetCountPlans.Map(endpoints);
        GetCountPlan.Map(endpoints);
        CancelCountPlan.Map(endpoints);
    }
}
