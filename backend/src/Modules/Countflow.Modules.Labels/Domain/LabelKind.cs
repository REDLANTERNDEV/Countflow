namespace Countflow.Modules.Labels.Domain;

/// <summary>
/// What a label is <i>about</i> — and the half of the label story that is code.
/// <para>
/// This is the same split ADR 0004 draws between <c>DecisionKind</c> and
/// <c>RuleSet</c>, and it is drawn here for the same reason. A kind fixes a
/// <b>binding contract</b>: which facts the system will go and fetch, from which
/// published surface, and what each of them is called. A template then decides
/// where those facts sit on a piece of paper, how big they are, and which of them
/// go into a barcode. The first is a change to this repository. The second is a
/// record, and that is the whole of what "held as data" buys.
/// </para>
/// <para>
/// <b>Why not let a kind be data too.</b> A kind invented in the console would be
/// a template that can reference anything, and a template that can reference
/// anything needs an expression language to reference it with. That is exactly the
/// trade <c>RuleDefinition</c> refuses: a scripting language cannot be checked
/// before it runs, and the check is the entire safety story here — a label
/// template is validated by rendering it, and there is nothing to render against
/// if the fields are not known in advance.
/// </para>
/// </summary>
public enum LabelKind
{
    /// <summary>
    /// The label on a pallet: its SSCC, and what is on it when that is a single
    /// thing. The first kind, and deliberately the first — a pallet arriving at a
    /// dock is the point in the warehouse where a licence plate stops being a
    /// column and has to become something a person can read.
    /// </summary>
    Pallet = 1
}
