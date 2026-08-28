using Countflow.Modules.Labels.Domain;
using Countflow.Modules.Labels.Kinds;
using Countflow.Modules.Labels.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Labels.Features.Templates;

/// <summary>One element as a supervisor supplies it.</summary>
public sealed record LabelElementDraft(
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

/// <summary>
/// Writes label templates.
/// <para>
/// The same four verbs a rule set has — create a draft, replace it, promote it,
/// archive it — and the same reason for each. <b>Only a draft is writable.</b> An
/// active template is what the floor is printing; an archived one is what a
/// <see cref="LabelPrint"/> from last quarter names, and that print is the system's
/// only account of a physical object somebody may still be holding. Editing either
/// would change the account of a label that has already left the building.
/// </para>
/// <para>
/// What this module does <i>not</i> borrow from rule sets is replay. A rule set can
/// be tried against history because a decision has candidates and a ranking to
/// compare; a label has neither. The equivalent safety here is
/// <see cref="LabelVocabulary.Renders"/>, which is cheaper and answers the only
/// question a template raises: would this print?
/// </para>
/// </summary>
public sealed class LabelTemplateEditor(LabelsDbContext dbContext, TimeProvider clock)
{
    public async Task<Result<LabelTemplate>> CreateDraftAsync(
        LabelKind kind,
        string name,
        Guid? warehouseId,
        int widthDots,
        int heightDots,
        int? darkness,
        string? notes,
        Guid? cloneFrom,
        IReadOnlyList<LabelElementDraft>? elements,
        CancellationToken cancellationToken)
    {
        if (!LabelVocabulary.HasVocabulary(kind))
        {
            return Result.Failure<LabelTemplate>(LabelsErrors.NoVocabulary(kind.ToString()));
        }

        IReadOnlyList<LabelElementDraft> source;

        if (cloneFrom is { } sourceId)
        {
            var original = await dbContext.LabelTemplates
                .AsNoTracking()
                .Include(t => t.Elements)
                .SingleOrDefaultAsync(t => t.Id == sourceId, cancellationToken);

            if (original is null) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotFound);

            // The kind comes from the original, exactly as a cloned rule set's
            // does. A pallet template cloned into a kind it was not written for
            // would reference fields that kind does not publish, and would be
            // refused a moment later by a check that could not explain itself.
            kind = original.Kind;

            widthDots = widthDots > 0 ? widthDots : original.WidthDots;
            heightDots = heightDots > 0 ? heightDots : original.HeightDots;
            darkness ??= original.Darkness;

            source = elements ?? [.. original.Elements.OrderBy(e => e.Order).Select(ToDraft)];
        }
        else
        {
            source = elements ?? [];
        }

        var draft = new LabelTemplate
        {
            Kind = kind,
            Name = name.Trim(),
            // Drafts take no version. A version is stamped onto every print and has
            // to mean "what the floor was running", so it is allocated at promotion
            // — an abandoned draft must not leave a hole in the numbering that a
            // print could be read as pointing into. Same rule as a rule set's.
            Version = 0,
            Status = LabelTemplateStatus.Draft,
            WarehouseId = warehouseId,
            WidthDots = widthDots,
            HeightDots = heightDots,
            Darkness = darkness,
            Notes = notes,
            CreatedAt = clock.GetUtcNow()
        };

        var applied = Apply(draft, source);
        if (applied.IsFailure) return Result.Failure<LabelTemplate>(applied.Error);

        dbContext.LabelTemplates.Add(draft);
        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    /// <summary>
    /// Replaces a draft's elements wholesale.
    /// <para>
    /// Wholesale rather than a patch per element, for the reason a rule set is
    /// replaced wholesale: draw order is part of the meaning — a box drawn after
    /// the text it frames erases the text — and a sequence of independent edits to
    /// an ordered list is how two people working an hour apart produce a layout
    /// neither of them designed.
    /// </para>
    /// </summary>
    public async Task<Result<LabelTemplate>> ReplaceAsync(
        Guid id,
        string? name,
        string? notes,
        int? widthDots,
        int? heightDots,
        int? darkness,
        IReadOnlyList<LabelElementDraft> elements,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.LabelTemplates
            .Include(t => t.Elements)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (draft is null) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotFound);
        if (draft.Status != LabelTemplateStatus.Draft) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotEditable);

        if (!string.IsNullOrWhiteSpace(name)) draft.Name = name.Trim();
        if (notes is not null) draft.Notes = notes;
        if (widthDots is { } width) draft.WidthDots = width;
        if (heightDots is { } height) draft.HeightDots = height;
        if (darkness is { } value) draft.Darkness = value;

        dbContext.LabelElements.RemoveRange(draft.Elements);
        draft.Elements.Clear();

        var applied = Apply(draft, elements);
        if (applied.IsFailure) return Result.Failure<LabelTemplate>(applied.Error);

        await dbContext.SaveChangesAsync(cancellationToken);

        return draft;
    }

    /// <summary>
    /// Puts a draft on the printer and archives what it replaces.
    /// <para>
    /// Archive first, then activate, in one transaction — and the "one active per
    /// kind per site" rule is enforced by the partial unique indexes rather than by
    /// this method, which is the right way round for the reason a rule set's
    /// promotion gives.
    /// </para>
    /// </summary>
    public async Task<Result<LabelTemplate>> PromoteAsync(Guid id, CancellationToken cancellationToken)
    {
        var draft = await dbContext.LabelTemplates
            .Include(t => t.Elements)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (draft is null) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotFound);
        if (draft.Status != LabelTemplateStatus.Draft) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotPromotable);
        if (draft.Elements.Count == 0) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateEmpty);

        // Checked again at the door, not only when it was saved. A draft written
        // before a field was renamed is a draft that no longer renders, and the
        // moment it goes on the floor is the last chance to find that out calmly.
        var renders = LabelVocabulary.Renders(draft);
        if (renders.IsFailure) return Result.Failure<LabelTemplate>(renders.Error);

        var now = clock.GetUtcNow();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var promoting = await dbContext.LabelTemplates
                .Include(t => t.Elements)
                .SingleAsync(t => t.Id == id, cancellationToken);

            var superseded = await dbContext.LabelTemplates
                .Where(t => t.Kind == promoting.Kind
                            && t.Status == LabelTemplateStatus.Active
                            && t.WarehouseId == promoting.WarehouseId)
                .ToListAsync(cancellationToken);

            foreach (var template in superseded)
            {
                template.Status = LabelTemplateStatus.Archived;
            }

            var highest = await dbContext.LabelTemplates
                .Where(t => t.Kind == promoting.Kind && t.WarehouseId == promoting.WarehouseId)
                .MaxAsync(t => (int?)t.Version, cancellationToken) ?? 0;

            promoting.Version = highest + 1;
            promoting.Status = LabelTemplateStatus.Active;
            promoting.ActivatedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        dbContext.ChangeTracker.Clear();

        return await dbContext.LabelTemplates
            .AsNoTracking()
            .Include(t => t.Elements)
            .SingleAsync(t => t.Id == id, cancellationToken);
    }

    /// <summary>Takes a template out of use. Never a delete — prints point at it.</summary>
    public async Task<Result<LabelTemplate>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await dbContext.LabelTemplates
            .Include(t => t.Elements)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template is null) return Result.Failure<LabelTemplate>(LabelsErrors.TemplateNotFound);

        template.Status = LabelTemplateStatus.Archived;

        await dbContext.SaveChangesAsync(cancellationToken);

        return template;
    }

    /// <summary>
    /// Writes the elements onto the template, refusing anything that would not
    /// print.
    /// <para>
    /// Two checks the renderer cannot make, then the renderer itself. It cannot see
    /// a duplicate draw order — it would happily render both and let the outcome
    /// depend on a tiebreak nobody chose — and it has no opinion on an empty
    /// template until it is asked to render one.
    /// </para>
    /// <para>
    /// <b>Elements are added through the <c>DbSet</c>, never through
    /// <c>template.Elements</c>.</b> The fourth instance of the trap
    /// <c>docs/gotchas.md</c> records, and written this way from the start rather
    /// than after it reached production the way <c>RuleSetEditor.Apply</c> did.
    /// The shape here is that file's exactly: correct on
    /// <see cref="CreateDraftAsync"/>, where the parent is new, and fatal on
    /// <see cref="ReplaceAsync"/>, where it is tracked — which is why the
    /// integration tests of this class exercise a <i>successful</i> replace and not
    /// only its refusals.
    /// </para>
    /// </summary>
    private Result Apply(LabelTemplate template, IReadOnlyList<LabelElementDraft> elements)
    {
        if (elements.Count == 0)
        {
            return Result.Failure(LabelsErrors.TemplateEmpty);
        }

        var orders = new HashSet<int>();
        var built = new List<LabelElement>(elements.Count);

        foreach (var draft in elements)
        {
            if (!orders.Add(draft.Order))
            {
                return Result.Failure(LabelsErrors.DuplicateElementOrder(draft.Order));
            }

            var element = new LabelElement
            {
                LabelTemplateId = template.Id,
                Type = draft.Type,
                Order = draft.Order,
                X = draft.X,
                Y = draft.Y,
                Rotation = string.IsNullOrWhiteSpace(draft.Rotation) ? "N" : draft.Rotation.Trim().ToUpperInvariant(),
                FieldCode = string.IsNullOrWhiteSpace(draft.FieldCode) ? null : draft.FieldCode.Trim(),
                Literal = draft.Literal,
                FontHeight = draft.FontHeight,
                FontWidth = draft.FontWidth,
                Symbology = draft.Symbology,
                BarcodeHeight = draft.BarcodeHeight,
                ShowInterpretation = draft.ShowInterpretation,
                WidthDots = draft.WidthDots,
                HeightDots = draft.HeightDots,
                ThicknessDots = draft.ThicknessDots
            };

            built.Add(element);
            dbContext.LabelElements.Add(element);
        }

        template.Elements.Clear();

        foreach (var element in built)
        {
            template.Elements.Add(element);
        }

        // And finally the real thing: would this print? Everything a field code, a
        // rotation, a geometry or a GS1 encoding could get wrong is caught here,
        // by the code that will actually do it.
        return LabelVocabulary.Renders(template);
    }

    private static LabelElementDraft ToDraft(LabelElement element) =>
        new(element.Type,
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
            element.ThicknessDots);
}
