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

/// <param name="AvailableConstraints">
/// Rule codes this build can run. Returned alongside the sets so the console can
/// show what a rule set <i>could</i> contain, not only what it does — a
/// supervisor cannot tune rules they have no way of discovering.
/// </param>
public sealed record RuleSetsResponse(
    IReadOnlyList<RuleSetResponse> Items,
    IReadOnlyList<string> AvailableConstraints,
    IReadOnlyList<string> AvailablePreferences);

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
                    PutawayRuleCatalogue.ConstraintCodes,
                    PutawayRuleCatalogue.PreferenceCodes));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("GetRuleSets")
            .WithSummary("Rule sets and the rule vocabulary this build can run.")
            .Produces<RuleSetsResponse>();
    }
}
