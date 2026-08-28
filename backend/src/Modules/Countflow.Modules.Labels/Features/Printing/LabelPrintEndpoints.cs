using System.Security.Claims;
using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Features.Printing;

/// <param name="Sscc">
/// The pallet to label. Omit it and the warehouse mints one — which is the ordinary
/// case at a dock, where a blind receipt arrives with no supplier licence plate on
/// it and the pallet has no readable identity until this call gives it one.
/// </param>
public sealed record PrintLabelRequest(
    LabelKind Kind,
    string WarehouseCode,
    string? Sscc = null);

/// <param name="Zpl">
/// The label, as ZPL II. Transport is deliberately not this API's business (see
/// <see cref="LabelGenerator"/>): a caller sends these bytes to a printer by
/// whatever route suits it, and generation is verifiable with no printer at all.
/// </param>
/// <param name="IsReprint">
/// True when this subject had been labelled before. Worth acting on: there are now
/// two physical objects claiming to be one pallet.
/// </param>
public sealed record PrintedLabelResponse(
    string Zpl,
    string Kind,
    string Sscc,
    Guid TemplateId,
    string TemplateName,
    int TemplateVersion,
    bool IsReprint,
    Guid? PrintId,
    DateTimeOffset PrintedAt);

/// <summary>
/// Generates a label and records that somebody made one.
/// <para>
/// A write, and it carries an idempotency key like every other write in this
/// system — for a reason particular to this one. A handheld's outbox retries, and a
/// retry that recorded a second print would report a reprint that never happened,
/// which is exactly the fact this table exists to be trusted about.
/// </para>
/// </summary>
public static class PrintLabel
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/labels/prints", async (
                PrintLabelRequest request,
                ClaimsPrincipal user,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                LabelGenerator generator,
                IWarehouseDirectory warehouses,
                ISsccMint mint,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    return Result.Failure<PrintedLabelResponse>(LabelsErrors.IdempotencyKeyRequired).ToHttpResult();
                }

                var actor = PostingActor.From(user);
                if (actor.IsFailure) return Result.Failure<PrintedLabelResponse>(actor.Error).ToHttpResult();

                var site = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
                if (site is null) return Result.Failure<PrintedLabelResponse>(LabelsErrors.WarehouseNotFound).ToHttpResult();

                var sscc = request.Sscc;

                if (string.IsNullOrWhiteSpace(sscc))
                {
                    // Minted on this company's own GS1 prefix, and refused outright
                    // when the site has none — a made-up prefix produces eighteen
                    // valid-looking digits belonging to somebody else's company.
                    // See ISsccMint.
                    var minted = await mint.MintAsync(site.Id, cancellationToken);
                    if (minted.IsFailure) return Result.Failure<PrintedLabelResponse>(minted.Error).ToHttpResult();

                    sscc = minted.Value;
                }

                var label = await generator.PrintAsync(
                    request.Kind, sscc, site.Id, actor.Value, idempotencyKey, cancellationToken);

                return label.IsFailure
                    ? Result.Failure<PrintedLabelResponse>(label.Error).ToHttpResult()
                    : Results.Ok(Response(label.Value));
            })
            .RequireAuthorization()
            .WithTags("Labels")
            .WithName("PrintLabel")
            .WithSummary("Generate a label and record the print. Mints an SSCC when none is supplied.")
            .Produces<PrintedLabelResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static PrintedLabelResponse Response(GeneratedLabel label) =>
        new(label.Zpl,
            label.Kind.ToString(),
            label.SubjectKey,
            label.TemplateId,
            label.TemplateName,
            label.TemplateVersion,
            label.IsReprint,
            label.PrintId,
            label.GeneratedAt);
}

public sealed record PreviewLabelRequest(
    LabelKind Kind,
    string WarehouseCode,
    string Sscc,
    Guid? TemplateId = null);

/// <summary>
/// Renders a label without recording anything, and without minting anything.
/// <para>
/// Separate from <see cref="PrintLabel"/> rather than a flag on it, and the
/// distinction is the same one <c>DecisionTrace.IsPreview</c> draws: asking what a
/// label would look like is not making one. A preview may name a draft — that is
/// most of its value — and it never creates a licence plate, because looking at a
/// label does not put an object in the building.
/// </para>
/// </summary>
public static class PreviewLabel
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/labels/renderings", async (
                PreviewLabelRequest request,
                LabelGenerator generator,
                IWarehouseDirectory warehouses,
                CancellationToken cancellationToken) =>
            {
                var site = await warehouses.FindByCodeAsync(request.WarehouseCode.Trim(), cancellationToken);
                if (site is null) return Result.Failure<PrintedLabelResponse>(LabelsErrors.WarehouseNotFound).ToHttpResult();

                var label = await generator.PreviewAsync(
                    request.Kind, request.Sscc, site.Id, request.TemplateId, cancellationToken);

                return label.IsFailure
                    ? Result.Failure<PrintedLabelResponse>(label.Error).ToHttpResult()
                    : Results.Ok(PrintLabel.Response(label.Value));
            })
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Supervisor))
            .WithTags("Labels")
            .WithName("PreviewLabel")
            .WithSummary("Render a label without recording a print or minting an SSCC.")
            .Produces<PrintedLabelResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}

public sealed record LabelPrintResponse(
    Guid Id,
    string Kind,
    string Sscc,
    Guid TemplateId,
    string TemplateName,
    int TemplateVersion,
    bool IsReprint,
    string PayloadHash,
    DateTimeOffset PrintedAt,
    string PrintedBy);

/// <summary>
/// What has been printed. The audit read, and the reason
/// <see cref="Domain.LabelPrint"/> is a table rather than nothing.
/// </summary>
public static class GetLabelPrints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/labels/prints", async (
                LabelsDbContext dbContext,
                string? sscc,
                LabelKind? kind,
                bool? reprintsOnly,
                int? limit,
                CancellationToken cancellationToken) =>
            {
                var query = dbContext.LabelPrints.AsNoTracking().AsQueryable();

                if (!string.IsNullOrWhiteSpace(sscc)) query = query.Where(p => p.SubjectKey == sscc.Trim());
                if (kind is { } k) query = query.Where(p => p.Kind == k);

                // The question this endpoint is really for. A supervisor asking
                // "what got printed twice this week?" should not have to filter it
                // out of everything that got printed once.
                if (reprintsOnly == true) query = query.Where(p => p.IsReprint);

                var prints = await query
                    .OrderByDescending(p => p.PrintedAt)
                    .Take(Math.Clamp(limit ?? 100, 1, 500))
                    .Select(p => new LabelPrintResponse(
                        p.Id,
                        p.Kind.ToString(),
                        p.SubjectKey,
                        p.LabelTemplateId,
                        p.TemplateName,
                        p.TemplateVersion,
                        p.IsReprint,
                        p.PayloadHash,
                        p.PrintedAt,
                        p.UserName))
                    .ToListAsync(cancellationToken);

                return Results.Ok(prints);
            })
            .RequireAuthorization()
            .WithTags("Labels")
            .WithName("GetLabelPrints")
            .WithSummary("Labels that were printed, newest first.")
            .Produces<IReadOnlyList<LabelPrintResponse>>();
    }
}
