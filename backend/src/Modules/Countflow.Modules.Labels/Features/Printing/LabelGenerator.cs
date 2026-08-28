using System.Security.Cryptography;
using System.Text;
using Countflow.Modules.Labels.Binding;
using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Kinds;
using Countflow.Modules.Labels.Persistence;
using Countflow.Modules.Labels.Rendering;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Features.Printing;

/// <param name="IsReprint">
/// True when a label for this subject had been printed before. The one fact this
/// module exists to surface — two physical objects each claiming to be the same
/// pallet is a real operational hazard, and the API says so rather than leaving it
/// to be discovered.
/// </param>
/// <param name="PrintId">
/// Null on a preview. A preview asserts nothing happened, so it records nothing
/// and has nothing to name.
/// </param>
public sealed record GeneratedLabel(
    string Zpl,
    LabelKind Kind,
    string SubjectKey,
    Guid TemplateId,
    string TemplateName,
    int TemplateVersion,
    bool IsReprint,
    Guid? PrintId,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Chooses the template, gathers the binding, renders the ZPL, and — when this is
/// a print rather than a preview — records that it happened.
/// <para>
/// <b>Transport is deliberately absent.</b> Nothing here opens a socket to port
/// 9100, talks to a print agent or knows a printer exists. A direct socket, a local
/// agent, WebUSB and cloud print are four options with genuinely different
/// trade-offs, and none of them is needed to verify a generator: the ZPL is the
/// output, and asserting on it needs no hardware. When transport lands it consumes
/// <see cref="GeneratedLabel.Zpl"/> and changes nothing here.
/// </para>
/// </summary>
public sealed class LabelGenerator(
    LabelsDbContext dbContext,
    PalletLabelBinder pallets,
    TimeProvider clock)
{
    /// <summary>
    /// Renders a label without recording anything. What the console's template
    /// editor calls, and what a supervisor uses to see their layout against a real
    /// pallet before putting it on the floor.
    /// </summary>
    public async Task<Result<GeneratedLabel>> PreviewAsync(
        LabelKind kind,
        string subjectKey,
        Guid warehouseId,
        Guid? templateId,
        CancellationToken cancellationToken)
    {
        var template = templateId is { } id
            ? await dbContext.LabelTemplates
                .AsNoTracking()
                .Include(t => t.Elements)
                .SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
            : await ActiveTemplateAsync(kind, warehouseId, cancellationToken);

        if (template is null)
        {
            return Result.Failure<GeneratedLabel>(
                templateId is null ? LabelsErrors.NoActiveTemplate(kind.ToString()) : LabelsErrors.TemplateNotFound);
        }

        // A preview may name a draft, and that is the point of it — the whole
        // value of previewing is seeing a layout that is not on the floor yet.
        // It creates no licence plate: looking at a label is not making one.
        var binding = await BindAsync(kind, subjectKey, warehouseId, createSubject: false, cancellationToken);
        if (binding.IsFailure) return Result.Failure<GeneratedLabel>(binding.Error);

        var zpl = ZplRenderer.Render(template, binding.Value);
        if (zpl.IsFailure) return Result.Failure<GeneratedLabel>(zpl.Error);

        return new GeneratedLabel(
            zpl.Value,
            kind,
            binding.Value.SubjectKey,
            template.Id,
            template.Name,
            template.Version,
            IsReprint: false,
            PrintId: null,
            clock.GetUtcNow());
    }

    /// <summary>
    /// Renders a label and records that somebody made one.
    /// <para>
    /// Only an active template, never a draft: a draft is a hypothesis, and a
    /// physical label produced from one would be a label nobody promoted. That is
    /// the same door <c>PromoteRuleSet</c> guards, and it is guarded here for the
    /// same reason — the difference between a document and a thing on a pallet.
    /// </para>
    /// </summary>
    public async Task<Result<GeneratedLabel>> PrintAsync(
        LabelKind kind,
        string subjectKey,
        Guid warehouseId,
        PostingActor actor,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var alreadyPrinted = await dbContext.LabelPrints
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

        if (alreadyPrinted is not null)
        {
            // A retry, not a second print. Re-rendered rather than stored, for the
            // reason given on LabelPrint.PayloadHash — but the *record* is the one
            // that already exists, so the reprint flag and the print id are the
            // original's and the operator is not accused of a duplicate.
            return await ReplayAsync(alreadyPrinted, cancellationToken);
        }

        var template = await ActiveTemplateAsync(kind, warehouseId, cancellationToken);

        if (template is null)
        {
            return Result.Failure<GeneratedLabel>(LabelsErrors.NoActiveTemplate(kind.ToString()));
        }

        // createSubject: printing the label is the moment the licence plate becomes
        // real. See ILicensePlateRegistry — from here on there is an object in the
        // building carrying this number, whether or not any stock is ever posted
        // against it.
        var binding = await BindAsync(kind, subjectKey, warehouseId, createSubject: true, cancellationToken);
        if (binding.IsFailure) return Result.Failure<GeneratedLabel>(binding.Error);

        var zpl = ZplRenderer.Render(template, binding.Value);
        if (zpl.IsFailure) return Result.Failure<GeneratedLabel>(zpl.Error);

        var isReprint = await dbContext.LabelPrints
            .AnyAsync(p => p.SubjectKey == binding.Value.SubjectKey && p.Kind == kind, cancellationToken);

        var print = new LabelPrint
        {
            Kind = kind,
            LabelTemplateId = template.Id,
            TemplateName = template.Name,
            TemplateVersion = template.Version,
            SubjectKey = binding.Value.SubjectKey,
            SubjectId = binding.Value.SubjectId,
            WarehouseId = warehouseId,
            IsReprint = isReprint,
            PayloadHash = Hash(zpl.Value),
            PrintedAt = clock.GetUtcNow(),
            UserId = actor.UserId,
            UserName = actor.UserName,
            IdempotencyKey = idempotencyKey
        };

        dbContext.LabelPrints.Add(print);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two retries of one command arriving together. The unique index on the
            // key arbitrates, and the loser reads what the winner recorded rather
            // than raising a conflict at an operator who pressed print once.
            dbContext.ChangeTracker.Clear();

            var raced = await dbContext.LabelPrints
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

            if (raced is null) throw;

            return await ReplayAsync(raced, cancellationToken);
        }

        return new GeneratedLabel(
            zpl.Value,
            kind,
            print.SubjectKey,
            template.Id,
            template.Name,
            template.Version,
            isReprint,
            print.Id,
            print.PrintedAt);
    }

    /// <summary>
    /// Re-renders the label a recorded print produced, from the template version it
    /// names.
    /// <para>
    /// This is what the version stamp is <i>for</i>, and the reason it is three
    /// columns rather than a foreign key: the template has moved on, the active one
    /// is a different layout, and a retry has to return what the first attempt
    /// returned. Reading the archived version by id is the only way to do that.
    /// </para>
    /// <para>
    /// The binding is gathered afresh, so a plate that has been filled since will
    /// re-render with its contents. That is deliberate — the alternative is storing
    /// the payload, and the hash is kept precisely so the two can be told apart when
    /// it matters.
    /// </para>
    /// </summary>
    private async Task<Result<GeneratedLabel>> ReplayAsync(LabelPrint print, CancellationToken cancellationToken)
    {
        var template = await dbContext.LabelTemplates
            .AsNoTracking()
            .Include(t => t.Elements)
            .SingleOrDefaultAsync(t => t.Id == print.LabelTemplateId, cancellationToken);

        if (template is null) return Result.Failure<GeneratedLabel>(LabelsErrors.TemplateNotFound);

        var binding = await BindAsync(print.Kind, print.SubjectKey, print.WarehouseId, false, cancellationToken);
        if (binding.IsFailure) return Result.Failure<GeneratedLabel>(binding.Error);

        var zpl = ZplRenderer.Render(template, binding.Value);
        if (zpl.IsFailure) return Result.Failure<GeneratedLabel>(zpl.Error);

        return new GeneratedLabel(
            zpl.Value,
            print.Kind,
            print.SubjectKey,
            print.LabelTemplateId,
            print.TemplateName,
            print.TemplateVersion,
            print.IsReprint,
            print.Id,
            print.PrintedAt);
    }

    private Task<Result<LabelBinding>> BindAsync(
        LabelKind kind,
        string subjectKey,
        Guid warehouseId,
        bool createSubject,
        CancellationToken cancellationToken) => kind switch
    {
        LabelKind.Pallet => pallets.BindAsync(subjectKey, warehouseId, createSubject, cancellationToken),
        _ => Task.FromResult(Result.Failure<LabelBinding>(LabelsErrors.NoVocabulary(kind.ToString())))
    };

    /// <summary>
    /// The template in force for a kind at a site: the site's own if it has one,
    /// otherwise the one that covers every warehouse. The same precedence a rule
    /// set uses, so there is one answer to "which configuration applies here?" in
    /// this system rather than two.
    /// </summary>
    private async Task<LabelTemplate?> ActiveTemplateAsync(
        LabelKind kind,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        var candidates = await dbContext.LabelTemplates
            .AsNoTracking()
            .Include(t => t.Elements)
            .Where(t => t.Kind == kind
                        && t.Status == LabelTemplateStatus.Active
                        && (t.WarehouseId == warehouseId || t.WarehouseId == null))
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(t => t.WarehouseId == warehouseId)
               ?? candidates.FirstOrDefault(t => t.WarehouseId is null);
    }

    private static string Hash(string zpl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(zpl)));
}
