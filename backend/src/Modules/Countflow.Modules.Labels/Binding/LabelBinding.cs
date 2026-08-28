using Countflow.Modules.Labels.Domain;

namespace Countflow.Modules.Labels.Binding;

/// <summary>One field a template of this kind may reference.</summary>
/// <param name="Code">
/// What a template writes, e.g. <c>pallet.sku</c>. Namespaced by kind so that a
/// field list read out of context still says what it belongs to.
/// </param>
/// <param name="IsAlwaysPresent">
/// False for a field that legitimately has no value on some subjects — the lot
/// number of a mixed pallet, the expiry of an item that is not date-tracked. A
/// template may still use it; it prints as nothing. This flag is for the editor,
/// so a supervisor laying out a label learns which fields can vanish before they
/// design a layout that falls apart when one does.
/// </param>
public sealed record LabelFieldDefinition(string Code, string Title, bool IsAlwaysPresent);

/// <summary>
/// Everything one label is going to say, gathered before anything is rendered.
/// <para>
/// <b>The caller does not assemble this.</b> A pallet label wants a SKU, a
/// description, a batch, an expiry, a quantity and an SSCC — which belong to
/// MasterData, Inventory and the receipt, across three schemas. Handing that job
/// to callers would mean every future caller reassembling it, each with its own
/// idea of which GTIN goes on a pallet and how a mixed one is handled, and the
/// first one to get it wrong prints a label that is wrong in a way nothing checks.
/// So the kind's binder asks — through published contracts, never through a join
/// (ADR 0002) — and the caller supplies only what a caller can know: which subject,
/// and at which site.
/// </para>
/// <para>
/// <b>A missing value is null, and null is a real answer.</b> A mixed pallet has no
/// single batch number, and the binding says so by holding null rather than by
/// holding the first one it found. The renderer prints nothing for it. That is the
/// whole of how this design keeps a label from asserting something the ledger does
/// not.
/// </para>
/// </summary>
public sealed class LabelBinding
{
    public required LabelKind Kind { get; init; }

    /// <summary>What is printed as the subject's identity — an SSCC on a pallet label.</summary>
    public required string SubjectKey { get; init; }

    public Guid? SubjectId { get; init; }

    public required Guid WarehouseId { get; init; }

    /// <summary>
    /// Field code to value. A code absent from this dictionary is a template
    /// referencing a field its kind does not publish, and it is a hard render
    /// failure; a code present with a null value is a field this subject has
    /// nothing to say about, and it prints as nothing. The two are not the same
    /// and are never collapsed.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> Fields { get; init; }

    /// <summary>
    /// The GS1 element string's contents, in the order they should be encoded.
    /// Empty when the subject has nothing that belongs in a GS1 symbol, which
    /// makes a template carrying a GS1 barcode fail to render — correctly, because
    /// a barcode with nothing in it is a label that looks complete and is not.
    /// </summary>
    public required IReadOnlyList<(string Ai, string Value)> Gs1Elements { get; init; }
}
