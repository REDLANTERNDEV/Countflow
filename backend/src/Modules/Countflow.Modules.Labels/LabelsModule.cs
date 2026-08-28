using Countflow.Modules.Labels.Features.Printing;
using Countflow.Modules.Labels.Features.Templates;
using Countflow.Modules.Labels.Kinds;
using Countflow.Modules.Labels.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Countflow.Modules.Labels;

/// <summary>
/// Printable identity: the templates a label is laid out with, and the record of
/// which labels were made.
/// <para>
/// <b>Why its own module rather than Rules or MasterData.</b> It has the outward
/// shape of Rules — versioned, promotable reference data with a draft/active/
/// archived life — but a label makes no judgement: there are no candidates, nothing
/// is excluded, and there is no trace, so putting it in Rules would be borrowing
/// the machinery for the resemblance rather than the reason. MasterData is the
/// closer fit for a template and the wrong home for a <see cref="Domain.LabelPrint"/>,
/// which is an operational event and not reference data at all — and MasterData has
/// no promotion lifecycle to lend. Under ADR 0002 the cost of a module is one
/// schema, one DbContext and one line in <c>ModuleCatalog</c>, and what it buys is
/// that neither of those two mistakes has to be made.
/// </para>
/// <para>
/// An orchestrator, like Inbound and Counting: it owns templates and prints, and
/// asks four other modules for everything a label actually says — through published
/// contracts, never through their tables.
/// </para>
/// <para>
/// <b>Publishes nothing.</b> Nothing in the warehouse consumes a label yet; when a
/// transport lands it will consume this module's HTTP surface like any other
/// client. A contract with no caller is a contract nobody has tested.
/// </para>
/// </summary>
public sealed class LabelsModule : ICountflowModule
{
    public string Name => "Labels";

    public string Schema => LabelsDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<LabelsDbContext>(connectionString, Schema);
        services.AddScoped<IModuleSeeder, LabelsSeeder>();

        services.AddScoped<LabelTemplateEditor>();
        services.AddScoped<LabelGenerator>();

        // One binder per kind, and each is code. This is the seam ADR 0004's
        // "kinds are code, sets are data" draws, applied to labels — see LabelKind.
        services.AddScoped<PalletLabelBinder>();

        services.TryAddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GetLabelKinds.Map(endpoints);
        GetLabelTemplates.Map(endpoints);
        GetLabelTemplate.Map(endpoints);
        CreateLabelTemplate.Map(endpoints);
        ReplaceLabelTemplate.Map(endpoints);
        PromoteLabelTemplate.Map(endpoints);
        ArchiveLabelTemplate.Map(endpoints);
        PreviewLabel.Map(endpoints);
        PrintLabel.Map(endpoints);
        GetLabelPrints.Map(endpoints);
    }
}
