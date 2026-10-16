using Countflow.Modules.Work.Features;
using Countflow.Modules.Work.Integration;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Work;

/// <summary>
/// Directed work: the queue an operator's handheld reads from, and the
/// confirmation that turns a job into a ledger entry.
/// <para>
/// Deliberately generic across task kinds rather than owned by the inbound loop.
/// Phase 3 raises only putaway, but picks (Phase 4) and cycle counts (Phase 5)
/// are the same shape, and a queue that belonged to inbound would have to be
/// rewritten or duplicated for each.
/// </para>
/// </summary>
public sealed class WorkModule : ICountflowModule
{
    public string Name => "Work";

    public string Schema => WorkDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<WorkDbContext>(connectionString, Schema);

        services.AddScoped<WorkTaskDescriber>();
        services.AddScoped<PutawayCompletions>();

        // The published boundary (ADR 0002): other modules may add to the queue
        // and nothing else. Claiming and completing are operator actions, and a
        // contract that exposed them would let another module move stock without
        // an operator ever confirming anything.
        services.AddScoped<IWorkQueue, WorkQueue>();
        services.AddScoped<PutawayReceipts>();
        services.AddScoped<IPutawayReceipts>(provider => provider.GetRequiredService<PutawayReceipts>());

        services.TryAddSingleton(TimeProvider.System);

        // No seeder. The queue starts empty by design — work is raised by the
        // inbound loop, not planted.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetWorkTasks.Map(endpoints);
        BlindBinCounts.Map(endpoints);
        GetWorkTask.Map(endpoints);
        ReslotPutaway.Map(endpoints);
        ClaimNextTask.Map(endpoints);
        ClaimTask.Map(endpoints);
        ReleaseTask.Map(endpoints);

        // Picks claimed as a run rather than one at a time, sequenced into a walk.
        // A separate verb from claim-next rather than a mode of it: the answer is a
        // different shape, and a scanner asking for one job should never be handed
        // twelve because somebody changed a default. See ClaimNextTour.
        ClaimNextTour.Map(endpoints);
        GetPickTours.Map(endpoints);
        GetPickTour.Map(endpoints);
        ReleasePickTour.Map(endpoints);
        CompleteWorkTask.Map(endpoints);

        // Work's slice of the accuracy screen. Override rates are this module's own
        // fact and nobody else can compute them, which is why the dashboard is
        // three fetches rather than one endpoint reaching across three schemas.
        GetWorkStatistics.Map(endpoints);
        GetWorkThroughput.Map(endpoints);
    }
}
