namespace Countflow.Modules.Labels.Domain;

/// <summary>
/// A label that was generated for somebody to put on something.
/// <para>
/// <b>Not a ledger movement, and the test is ADR 0001's own.</b> Printing moves no
/// stock: a stocktake would find exactly what it found before, so writing a
/// movement would put a fact that is not about a quantity into an append-only
/// record of quantities. That is the same reasoning that keeps a reservation and a
/// no-variance count out of the ledger, applied a third time.
/// </para>
/// <para>
/// <b>Recorded anyway, and this is the part worth arguing for.</b> A reprint means
/// there are now two physical objects in the building each claiming to be the same
/// pallet, and that is a real operational fact with a real failure behind it —
/// goods received twice against one licence plate, a pallet despatched under a
/// number that is still on a label in the yard. "Who printed the second one, and
/// when" is the first question anybody asks afterwards, and a generator that were
/// a pure function could not answer it. The cost is one insert on a path that is
/// already writing.
/// </para>
/// <para>
/// Never updated, for the reason a decision trace is never updated: it is evidence
/// about something that has already left the building.
/// </para>
/// </summary>
public sealed class LabelPrint
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required LabelKind Kind { get; init; }

    public required Guid LabelTemplateId { get; init; }

    /// <summary>
    /// Copied, not joined — and the same three columns <c>DecisionTrace</c> keeps
    /// for its rule set, for the same reason. A template will be renamed and
    /// superseded, and a print from 2026 has to go on saying which layout produced
    /// the piece of paper somebody is holding.
    /// </summary>
    public required string TemplateName { get; init; }

    public required int TemplateVersion { get; init; }

    /// <summary>
    /// What the label is about, in the form printed on it — an SSCC for a pallet
    /// label. A string rather than a foreign key because it is the identifier that
    /// exists physically, and because the plate row it names lives in another
    /// schema (ADR 0002).
    /// </summary>
    public required string SubjectKey { get; init; }

    /// <summary>
    /// The licence plate this label named, when the print resolved one. Kept beside
    /// <see cref="SubjectKey"/> rather than instead of it: the key is what is on the
    /// paper and the id is what the rest of the system indexes by.
    /// </summary>
    public Guid? SubjectId { get; init; }

    public required Guid WarehouseId { get; init; }

    /// <summary>
    /// True when a label for this subject had already been printed. Derived at
    /// print time and stored, not computed on read, because it is the thing this
    /// table exists to record and a query that reconstructed it would depend on
    /// rows nobody has promised not to archive.
    /// </summary>
    public required bool IsReprint { get; init; }

    /// <summary>
    /// SHA-256 of the emitted ZPL, hex. The payload itself is not stored — a label
    /// is regenerable from its template version and its subject, and keeping a few
    /// hundred bytes of markup per pallet forever buys nothing the hash does not.
    /// What the hash buys is the ability to answer "did these two labels say the
    /// same thing?", which is the question a duplicate SSCC raises.
    /// </summary>
    public required string PayloadHash { get; init; }

    public required DateTimeOffset PrintedAt { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Copied for the reason <c>StockMovement.UserName</c> is copied.</summary>
    public required string UserName { get; init; }

    /// <summary>
    /// The caller's key. A retried print returns the label it already made rather
    /// than recording a second one — a handheld's outbox retries, and a retry that
    /// counted as a reprint would accuse an operator of something they did not do.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}
