using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.CycleCount;
using Countflow.Modules.Rules.Features.Allocation;
using Countflow.Modules.Rules.Features.CycleCount;
using Countflow.Modules.Rules.Features.Putaway;
using Countflow.Modules.Rules.Features.RuleSets;
using Countflow.Modules.Rules.Features.Traces;
using Countflow.Modules.Rules.Integration;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Putaway;
using Countflow.Modules.Rules.Replay;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Rules;

/// <summary>
/// Directed decisions and the traces that justify them (ADR 0004).
/// <para>
/// The second of the three product pillars. It owns the rule sets, the shared
/// evaluation, and the record of every choice made on an operator's behalf.
/// </para>
/// </summary>
public sealed class RulesModule : ICountflowModule
{
    public string Name => "Rules";

    public string Schema => RulesDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<RulesDbContext>(connectionString, Schema);
        services.AddScoped<IModuleSeeder, RulesSeeder>();

        services.AddScoped<DecisionJournal>();
        services.AddScoped<RuleSetEditor>();
        services.AddScoped<PutawayAdvisor>();
        services.AddScoped<AllocationAdvisor>();
        services.AddScoped<CountPolicyAdvisor>();

        // The published boundary (ADR 0002). PutawayAdvisor is also registered by
        // its concrete type, because the preview endpoint in this module needs the
        // extra method the contract deliberately does not expose — asking the
        // rules a hypothetical is not something another module should be able to
        // record traces with.
        services.AddScoped<IPutawayAdvisor>(sp => sp.GetRequiredService<PutawayAdvisor>());
        services.AddScoped<IAllocationAdvisor>(sp => sp.GetRequiredService<AllocationAdvisor>());
        services.AddScoped<ICountPolicy>(sp => sp.GetRequiredService<CountPolicyAdvisor>());
        services.AddScoped<IDecisionTraceReader, DecisionTraceReader>();

        // Phase 6's, and published for the same reason the advisors are: the module
        // that runs an experiment and keeps its results is not this one. Rules knows
        // how to ask a question again — it owns the vocabularies and the traces —
        // and what became of the answer belongs to whoever asked.
        services.AddScoped<IShadowReplay, ShadowReplay>();

        services.TryAddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetRuleSets.Map(endpoints);
        CreateRuleSet.Map(endpoints);
        ReplaceRuleSet.Map(endpoints);
        PromoteRuleSet.Map(endpoints);
        ArchiveRuleSet.Map(endpoints);
        GetDecisionTrace.Map(endpoints);
        PreviewPutaway.Map(endpoints);
        PreviewAllocation.Map(endpoints);
        PreviewCountPolicy.Map(endpoints);
    }
}
