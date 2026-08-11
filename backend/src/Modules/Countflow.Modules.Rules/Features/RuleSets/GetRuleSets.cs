using Countflow.Modules.Rules.Allocation;
using Countflow.Modules.Rules.Domain;
using Countflow.Modules.Rules.Persistence;
using Countflow.Modules.Rules.Putaway;
using Countflow.SharedKernel.Rules;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Rules.Features.RuleSets;

public sealed record RuleResponse(
    string Code,
    RuleRole Role,
    int Order,
    decimal Weight,
    string? Parameters,
    bool IsEnabled);

public sealed record RuleSetResponse(
    Guid Id,
    DecisionKind Kind,
    string Name,
    int Version,
    RuleSetStatus Status,
    Guid? WarehouseId,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<RuleResponse> Rules);

/// <summary>
/// Rule codes this build can run, for one decision kind.
/// <para>
/// Per kind rather than one flat list, and Phase 4 is what forced that: the two
/// vocabularies are not interchangeable — an allocation rule is typed against
/// holdings and a putaway rule against locations, so neither can appear in the
/// other's set — and a console offering a supervisor
/// <c>putaway.temperature_match</c> as something to add to an allocation set would
/// be offering a rule set the catalogue is going to refuse.
/// </para>
/// </summary>
public sealed record RuleVocabularyResponse(
    DecisionKind Kind,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Preferences);

/// <param name="Vocabulary">
/// What a rule set <i>could</i> contain, not only what it does — a supervisor
/// cannot tune rules they have no way of discovering.
/// </param>
public sealed record RuleSetsResponse(
    IReadOnlyList<RuleSetResponse> Items,
    IReadOnlyList<RuleVocabularyResponse> Vocabulary);

/// <summary>
/// The rule sets the warehouse is running, and the vocabulary they are built
/// from. Supervisor-and-above: these decide where every pallet in the building
/// goes.
/// </summary>
public static class GetRuleSets
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/rules/sets", async (
                RulesDbContext dbContext,
                CancellationToken cancellationToken,
                DecisionKind? kind = null) =>
            {
                var sets = await dbContext.RuleSets
                    .AsNoTracking()
                    .Include(r => r.Rules)
                    .Where(r => kind == null || r.Kind == kind)
                    .OrderBy(r => r.Kind)
                    .ThenBy(r => r.Name)
                    .ThenByDescending(r => r.Version)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new RuleSetsResponse(
                    [
                        .. sets.Select(set => new RuleSetResponse(
                            set.Id,
                            set.Kind,
                            set.Name,
                            set.Version,
                            set.Status,
                            set.WarehouseId,
                            set.Notes,
                            set.CreatedAt,
                            set.ActivatedAt,
                            [
                                .. set.Rules
                                    .OrderBy(r => r.Order)
                                    .Select(r => new RuleResponse(
                                        r.Code, r.Role, r.Order, r.Weight, r.Parameters, r.IsEnabled))
                            ]))
                    ],
                    [
                        new RuleVocabularyResponse(
                            DecisionKind.Putaway,
                            PutawayRuleCatalogue.ConstraintCodes,
                            PutawayRuleCatalogue.PreferenceCodes),
                        new RuleVocabularyResponse(
                            DecisionKind.Allocation,
                            AllocationRuleCatalogue.ConstraintCodes,
                            AllocationRuleCatalogue.PreferenceCodes)
                    ]));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("GetRuleSets")
            .WithSummary("Rule sets and the rule vocabulary this build can run.")
            .Produces<RuleSetsResponse>();
    }
}
