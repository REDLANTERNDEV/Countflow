namespace Countflow.Modules.Labels.Domain;

/// <summary>
/// Where a label template is in its life. The same three states a rule set has,
/// and borrowed knowingly rather than by reflex.
/// <para>
/// A rule set needs them because an archived set is what a decision trace from
/// last quarter points at. A label template needs them because an archived
/// template is what a <see cref="LabelPrint"/> from last quarter points at — and
/// that print is the system's only account of a physical object that may still be
/// stuck to a pallet in somebody's warehouse. Both are evidence rather than
/// documents; neither is ever deleted.
/// </para>
/// </summary>
public enum LabelTemplateStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3
}

/// <summary>
/// A named, versioned layout for one kind of label. Data, not code.
/// <para>
/// The thing this buys is <b>layout and site variation without a deploy</b>: a
/// customer who wants their own part number in 40-point type, a site whose printer
/// takes 4×6 rather than 4×8, a chilled depot that wants the temperature class
/// across the top. Those are edits that otherwise happen at six in the morning
/// with a pallet waiting, and they are edits to rows here.
/// </para>
/// <para>
/// What it does <b>not</b> buy is a new label <see cref="LabelKind"/> — see there
/// for why that stays code. This is the narrower claim than the one the issue
/// behind this module started from, and it is the one that survives contact with
/// <c>RuleDefinition</c>'s argument.
/// </para>
/// <para>
/// A template carries no decision trace and never will. It makes no judgement:
/// there are no candidates, nothing is excluded, and "why this template?" has
/// exactly one answer — it was the active one for this kind and this site — which
/// is why <see cref="LabelPrint"/> records the answer as three columns rather than
/// as a trace.
/// </para>
/// </summary>
public sealed class LabelTemplate
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required LabelKind Kind { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// Increments per promotion, and stamped onto every print. A label printed
    /// last year came off a version that has since moved on, and this is what lets
    /// somebody holding that piece of paper find out what produced it.
    /// </summary>
    public required int Version { get; set; }

    public LabelTemplateStatus Status { get; set; } = LabelTemplateStatus.Draft;

    /// <summary>
    /// Null means "every warehouse". A site with a different printer, different
    /// stock or a different customer gets its own, and the site-specific one wins
    /// over the default — the same precedence a rule set uses.
    /// </summary>
    public Guid? WarehouseId { get; set; }

    /// <summary>
    /// Printable width in dots. Dots and not millimetres, because ZPL positions in
    /// dots and a template that stored millimetres would have to know the printer's
    /// resolution to place anything — which is a fact about the device, and the
    /// device is not modelled here at all (transport is deliberately out of scope).
    /// </summary>
    public required int WidthDots { get; set; }

    public required int HeightDots { get; set; }

    /// <summary>
    /// Print darkness, 0–30, or null to leave the printer on whatever it is set to.
    /// Null is the better default: a warehouse that has tuned its printers for its
    /// own label stock does not want a template overriding them.
    /// </summary>
    public int? Darkness { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ActivatedAt { get; set; }

    public ICollection<LabelElement> Elements { get; init; } = [];
}
