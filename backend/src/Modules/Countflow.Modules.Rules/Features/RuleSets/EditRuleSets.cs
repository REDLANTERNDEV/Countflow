using Countflow.Modules.Rules.Domain;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Rules;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.RuleSets;

public sealed record RuleDraftRequest(
    string Code,
    int Order,
    decimal Weight = 0m,
    string? Parameters = null,
    bool IsEnabled = true);

/// <param name="CloneFromRuleSetId">
/// The set to copy. When present, <paramref name="Kind"/> is taken from the
/// original — a copy of a putaway set is a putaway set, and letting the two
/// disagree would produce a draft the catalogue refuses on first use.
/// </param>
public sealed record CreateRuleSetRequest(
    DecisionKind Kind,
    string Name,
    Guid? WarehouseId = null,
    string? Notes = null,
    Guid? CloneFromRuleSetId = null,
    IReadOnlyList<RuleDraftRequest>? Rules = null);

public sealed record ReplaceRuleSetRequest(
    IReadOnlyList<RuleDraftRequest> Rules,
    string? Name = null,
    string? Notes = null);

/// <summary>
/// Creates a draft rule set, usually as a copy of one that is already running.
/// </summary>
public static class CreateRuleSet
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rules/sets", async (
                CreateRuleSetRequest request,
                RuleSetEditor editor,
                CancellationToken cancellationToken) =>
            {
                var draft = await editor.CreateDraftAsync(
                    request.Kind,
                    request.Name,
                    request.WarehouseId,
                    request.Notes,
                    request.CloneFromRuleSetId,
                    Convert(request.Rules),
                    cancellationToken);

                return draft.IsFailure
                    ? Result.Failure<RuleSetResponse>(draft.Error).ToHttpResult()
                    : Results.Ok(RuleSetResponse.Of(draft.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("CreateRuleSet")
            .WithSummary("Create a draft rule set, optionally cloned from an existing one.")
            .Produces<RuleSetResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static IReadOnlyList<RuleDraft>? Convert(IReadOnlyList<RuleDraftRequest>? rules) =>
        rules is null
            ? null
            : [.. rules.Select(rule => new RuleDraft(rule.Code, rule.Order, rule.Weight, rule.Parameters, rule.IsEnabled))];
}

/// <summary>
/// Replaces a draft's rules. Refused for an active or archived set, which is
/// evidence behind decisions already made rather than a document.
/// </summary>
public static class ReplaceRuleSet
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/rules/sets/{id:guid}", async (
                Guid id,
                ReplaceRuleSetRequest request,
                RuleSetEditor editor,
                CancellationToken cancellationToken) =>
            {
                var updated = await editor.ReplaceAsync(
                    id,
                    request.Name,
                    request.Notes,
                    CreateRuleSet.Convert(request.Rules) ?? [],
                    cancellationToken);

                return updated.IsFailure
                    ? Result.Failure<RuleSetResponse>(updated.Error).ToHttpResult()
                    : Results.Ok(RuleSetResponse.Of(updated.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("ReplaceRuleSet")
            .WithSummary("Replace a draft rule set's rules.")
            .Produces<RuleSetResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>
/// Puts a draft on the floor and archives what it replaces.
/// <para>
/// Admin only, and the one endpoint in this module that is. Everything else here
/// authors a hypothesis; this is the one that changes where every pallet in the
/// building goes tonight.
/// </para>
/// </summary>
public static class PromoteRuleSet
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rules/sets/{id:guid}/promote", async (
                Guid id,
                RuleSetEditor editor,
                CancellationToken cancellationToken) =>
            {
                var promoted = await editor.PromoteAsync(id, cancellationToken);

                return promoted.IsFailure
                    ? Result.Failure<RuleSetResponse>(promoted.Error).ToHttpResult()
                    : Results.Ok(RuleSetResponse.Of(promoted.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin))
            .WithTags("Rules")
            .WithName("PromoteRuleSet")
            .WithSummary("Activate a draft rule set, archiving the one it replaces.")
            .Produces<RuleSetResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>Takes a set out of use. Never a delete — traces point at it.</summary>
public static class ArchiveRuleSet
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rules/sets/{id:guid}/archive", async (
                Guid id,
                RuleSetEditor editor,
                CancellationToken cancellationToken) =>
            {
                var archived = await editor.ArchiveAsync(id, cancellationToken);

                return archived.IsFailure
                    ? Result.Failure<RuleSetResponse>(archived.Error).ToHttpResult()
                    : Results.Ok(RuleSetResponse.Of(archived.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Rules")
            .WithName("ArchiveRuleSet")
            .WithSummary("Archive a rule set without deleting it.")
            .Produces<RuleSetResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
