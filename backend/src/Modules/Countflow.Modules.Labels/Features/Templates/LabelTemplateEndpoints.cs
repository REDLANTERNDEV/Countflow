using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Kinds;
using Countflow.Modules.Labels.Persistence;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Features.Templates;

public sealed record LabelElementRequest(
    LabelElementType Type,
    int Order,
    int X,
    int Y,
    string Rotation = "N",
    string? FieldCode = null,
    string? Literal = null,
    int? FontHeight = null,
    int? FontWidth = null,
    BarcodeSymbology? Symbology = null,
    int? BarcodeHeight = null,
    bool ShowInterpretation = true,
    int? WidthDots = null,
    int? HeightDots = null,
    int? ThicknessDots = null);

/// <param name="CloneFromTemplateId">
/// The template to copy. When present, <paramref name="Kind"/> is taken from the
/// original — and cloning is the expected path, because a supervisor changing a
/// label is moving one field on a layout of fourteen and retyping the other
/// thirteen is how a template loses an element nobody meant to drop.
/// </param>
public sealed record CreateLabelTemplateRequest(
    LabelKind Kind,
    string Name,
    int WidthDots = 0,
    int HeightDots = 0,
    Guid? WarehouseId = null,
    int? Darkness = null,
    string? Notes = null,
    Guid? CloneFromTemplateId = null,
    IReadOnlyList<LabelElementRequest>? Elements = null);

public sealed record ReplaceLabelTemplateRequest(
    IReadOnlyList<LabelElementRequest> Elements,
    string? Name = null,
    string? Notes = null,
    int? WidthDots = null,
    int? HeightDots = null,
    int? Darkness = null);

public sealed record LabelElementResponse(
    Guid Id,
    string Type,
    int Order,
    int X,
    int Y,
    string Rotation,
    string? FieldCode,
    string? Literal,
    int? FontHeight,
    int? FontWidth,
    string? Symbology,
    int? BarcodeHeight,
    bool ShowInterpretation,
    int? WidthDots,
    int? HeightDots,
    int? ThicknessDots);

public sealed record LabelTemplateResponse(
    Guid Id,
    string Kind,
    string Name,
    int Version,
    string Status,
    Guid? WarehouseId,
    int WidthDots,
    int HeightDots,
    int? Darkness,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<LabelElementResponse> Elements)
{
    public static LabelTemplateResponse Of(LabelTemplate template) =>
        new(template.Id,
            template.Kind.ToString(),
            template.Name,
            template.Version,
            template.Status.ToString(),
            template.WarehouseId,
            template.WidthDots,
            template.HeightDots,
            template.Darkness,
            template.Notes,
            template.CreatedAt,
            template.ActivatedAt,
            [
                .. template.Elements
                    .OrderBy(element => element.Order)
                    .Select(element => new LabelElementResponse(
                        element.Id,
                        element.Type.ToString(),
                        element.Order,
                        element.X,
                        element.Y,
                        element.Rotation,
                        element.FieldCode,
                        element.Literal,
                        element.FontHeight,
                        element.FontWidth,
                        element.Symbology?.ToString(),
                        element.BarcodeHeight,
                        element.ShowInterpretation,
                        element.WidthDots,
                        element.HeightDots,
                        element.ThicknessDots))
            ]);
}

/// <summary>Creates a draft template, usually as a copy of one already printing.</summary>
public static class CreateLabelTemplate
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/labels/templates", async (
                CreateLabelTemplateRequest request,
                LabelTemplateEditor editor,
                CancellationToken cancellationToken) =>
            {
                var draft = await editor.CreateDraftAsync(
                    request.Kind,
                    request.Name,
                    request.WarehouseId,
                    request.WidthDots,
                    request.HeightDots,
                    request.Darkness,
                    request.Notes,
                    request.CloneFromTemplateId,
                    Convert(request.Elements),
                    cancellationToken);

                return draft.IsFailure
                    ? Result.Failure<LabelTemplateResponse>(draft.Error).ToHttpResult()
                    : Results.Ok(LabelTemplateResponse.Of(draft.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Labels")
            .WithName("CreateLabelTemplate")
            .WithSummary("Create a draft label template, optionally cloned from an existing one.")
            .Produces<LabelTemplateResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static IReadOnlyList<LabelElementDraft>? Convert(IReadOnlyList<LabelElementRequest>? elements) =>
        elements is null
            ? null
            : [.. elements.Select(element => new LabelElementDraft(
                element.Type,
                element.Order,
                element.X,
                element.Y,
                element.Rotation,
                element.FieldCode,
                element.Literal,
                element.FontHeight,
                element.FontWidth,
                element.Symbology,
                element.BarcodeHeight,
                element.ShowInterpretation,
                element.WidthDots,
                element.HeightDots,
                element.ThicknessDots))];
}

/// <summary>
/// Replaces a draft's elements. Refused for an active or archived template, which
/// is the account of labels already printed rather than a document.
/// </summary>
public static class ReplaceLabelTemplate
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/labels/templates/{id:guid}", async (
                Guid id,
                ReplaceLabelTemplateRequest request,
                LabelTemplateEditor editor,
                CancellationToken cancellationToken) =>
            {
                var updated = await editor.ReplaceAsync(
                    id,
                    request.Name,
                    request.Notes,
                    request.WidthDots,
                    request.HeightDots,
                    request.Darkness,
                    CreateLabelTemplate.Convert(request.Elements) ?? [],
                    cancellationToken);

                return updated.IsFailure
                    ? Result.Failure<LabelTemplateResponse>(updated.Error).ToHttpResult()
                    : Results.Ok(LabelTemplateResponse.Of(updated.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Labels")
            .WithName("ReplaceLabelTemplate")
            .WithSummary("Replace a draft label template's elements.")
            .Produces<LabelTemplateResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>
/// Puts a draft on the printers and archives what it replaces.
/// <para>
/// Admin only, matching rule-set promotion. It is a smaller act than changing
/// where every pallet goes tonight, but it is the same kind of act: from here on
/// every physical label the building produces comes off this layout.
/// </para>
/// </summary>
public static class PromoteLabelTemplate
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/labels/templates/{id:guid}/promote", async (
                Guid id,
                LabelTemplateEditor editor,
                CancellationToken cancellationToken) =>
            {
                var promoted = await editor.PromoteAsync(id, cancellationToken);

                return promoted.IsFailure
                    ? Result.Failure<LabelTemplateResponse>(promoted.Error).ToHttpResult()
                    : Results.Ok(LabelTemplateResponse.Of(promoted.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin))
            .WithTags("Labels")
            .WithName("PromoteLabelTemplate")
            .WithSummary("Activate a draft label template, archiving the one it replaces.")
            .Produces<LabelTemplateResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

/// <summary>Takes a template out of use. Never a delete — prints point at it.</summary>
public static class ArchiveLabelTemplate
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/labels/templates/{id:guid}/archive", async (
                Guid id,
                LabelTemplateEditor editor,
                CancellationToken cancellationToken) =>
            {
                var archived = await editor.ArchiveAsync(id, cancellationToken);

                return archived.IsFailure
                    ? Result.Failure<LabelTemplateResponse>(archived.Error).ToHttpResult()
                    : Results.Ok(LabelTemplateResponse.Of(archived.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Labels")
            .WithName("ArchiveLabelTemplate")
            .WithSummary("Archive a label template without deleting it.")
            .Produces<LabelTemplateResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

public static class GetLabelTemplates
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/labels/templates", async (
                LabelsDbContext dbContext,
                LabelKind? kind,
                LabelTemplateStatus? status,
                Guid? warehouseId,
                CancellationToken cancellationToken) =>
            {
                var query = dbContext.LabelTemplates
                    .AsNoTracking()
                    .Include(template => template.Elements)
                    .AsQueryable();

                if (kind is { } k) query = query.Where(template => template.Kind == k);
                if (status is { } s) query = query.Where(template => template.Status == s);
                if (warehouseId is { } w) query = query.Where(template => template.WarehouseId == w);

                var templates = await query
                    .OrderBy(template => template.Kind)
                    .ThenByDescending(template => template.Version)
                    .ThenByDescending(template => template.CreatedAt)
                    .ToListAsync(cancellationToken);

                return Results.Ok(templates.Select(LabelTemplateResponse.Of).ToList());
            })
            .RequireAuthorization()
            .WithTags("Labels")
            .WithName("GetLabelTemplates")
            .WithSummary("List label templates.")
            .Produces<IReadOnlyList<LabelTemplateResponse>>();
    }
}

public static class GetLabelTemplate
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/labels/templates/{id:guid}", async (
                Guid id,
                LabelsDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var template = await dbContext.LabelTemplates
                    .AsNoTracking()
                    .Include(t => t.Elements)
                    .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

                return template is null
                    ? Result.Failure<LabelTemplateResponse>(LabelsErrors.TemplateNotFound).ToHttpResult()
                    : Results.Ok(LabelTemplateResponse.Of(template));
            })
            .RequireAuthorization()
            .WithTags("Labels")
            .WithName("GetLabelTemplate")
            .WithSummary("One label template, with its elements.")
            .Produces<LabelTemplateResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

/// <param name="IsAlwaysPresent">
/// False for a field that some subjects have nothing to say about. The editor
/// shows it, so a supervisor knows which parts of their layout can come out blank
/// before they design around them.
/// </param>
public sealed record LabelFieldResponse(string Code, string Title, bool IsAlwaysPresent);

public sealed record LabelKindResponse(string Kind, IReadOnlyList<LabelFieldResponse> Fields);

/// <summary>
/// What a template of each kind may reference.
/// <para>
/// The published half of the seam where data stops and code starts: everything in
/// this response is a template's to arrange, and anything not in it needs a change
/// to the server. Same role <c>GET /rules/vocabulary</c> plays for rule sets.
/// </para>
/// </summary>
public static class GetLabelKinds
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/labels/kinds", () =>
                Results.Ok(LabelVocabulary.Kinds
                    .Select(kind => new LabelKindResponse(
                        kind.ToString(),
                        [
                            .. LabelVocabulary.Fields(kind)
                                .Select(field => new LabelFieldResponse(field.Code, field.Title, field.IsAlwaysPresent))
                        ]))
                    .ToList()))
            .RequireAuthorization()
            .WithTags("Labels")
            .WithName("GetLabelKinds")
            .WithSummary("The label kinds, and the fields a template of each may print.")
            .Produces<IReadOnlyList<LabelKindResponse>>();
    }
}
