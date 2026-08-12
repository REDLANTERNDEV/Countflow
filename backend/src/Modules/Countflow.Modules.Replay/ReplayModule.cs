using Countflow.Modules.Replay.Features;
using Countflow.Modules.Replay.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Replay;

/// <summary>
/// Shadow replay: what a candidate rule set would have done to work the warehouse
/// has already done (ADR 0004).
/// <para>
/// The third product pillar's record-keeping, and the fourth orchestrator of the
/// shape Inbound, Outbound and Counting established. It owns replay runs and
/// nothing else — the decisions are Rules', the history they are rebuilt from is
/// Inventory's, and the topology and catalogue behind them are MasterData's, each
/// through a published contract. It writes no rule, no trace and no stock figure,
/// which is the discipline every phase since the ledger has kept.
/// </para>
/// <para>
/// Publishes nothing. Its output is an argument, and arguments are read by people.
/// </para>
/// <para>
/// The thing it genuinely owns is that the experiment happened and what it said at
/// the time — which is why a run is a table rather than a report. A rule set gets
/// promoted on Tuesday and the picking figures are worse on Friday; "run the replay
/// again" answers a different question, because the window and the warehouse have
/// both moved on.
/// </para>
/// </summary>
public sealed class ReplayModule : ICountflowModule
{
    public string Name => "Replay";

    public string Schema => ReplayDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<ReplayDbContext>(connectionString, Schema);

        services.AddScoped<ReplayRunner>();

        services.TryAddSingleton(TimeProvider.System);

        // No seeder. A replay is an experiment somebody chose to run, and a seeded
        // one would be a conclusion about a warehouse that has done no work yet.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        CreateReplayRun.Map(endpoints);
        GetReplayRuns.Map(endpoints);
        GetReplayRun.Map(endpoints);
    }
}
