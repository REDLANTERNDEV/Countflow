using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Rules;

/// <summary>
/// Every way a rule evaluation can legitimately fail, as a stable code. Clients
/// render wording from <c>packages/i18n</c> keyed on the code.
/// </summary>
public static class RulesErrors
{
    public static Error NoActiveRuleSet(string kind) =>
        Error.Failure(
            "rules.no_active_rule_set",
            $"No active rule set for {kind} decisions. The migrator seeds one; a warehouse with none has been misconfigured.");

    /// <summary>
    /// A rule row naming an implementation that no longer exists. Deliberately
    /// fatal rather than skipped: quietly dropping an unrecognised constraint is
    /// how a chilled pallet ends up in an ambient aisle because somebody renamed a
    /// class.
    /// </summary>
    public static Error UnknownRule(string code) =>
        Error.Failure(
            "rules.unknown_rule",
            $"Rule '{code}' has no implementation in this build. Refusing to evaluate a rule set with a rule missing.");

    public static Error InvalidRuleParameters(string code, string detail) =>
        Error.Failure("rules.invalid_rule_parameters", $"Rule '{code}' has unusable parameters: {detail}");

    public static readonly Error ItemNotFound =
        Error.NotFound("rules.item_not_found", "No item with that identifier.");

    public static readonly Error LocationNotFound =
        Error.NotFound("rules.location_not_found", "No location with that identifier or code.");

    public static readonly Error WarehouseNotFound =
        Error.NotFound("rules.warehouse_not_found", "No warehouse with that code.");

    public static readonly Error TraceNotFound =
        Error.NotFound("rules.trace_not_found", "No decision trace with that identifier.");

    public static readonly Error UnknownActor =
        Error.Unauthorized("rules.unknown_actor", "The access token carries no usable user identity.");

    public static readonly Error QuantityNotPositive =
        Error.Validation("rules.quantity_not_positive", "Quantity must be greater than zero.");

    // ---- Editing rule sets (Phase 6) ----

    public static readonly Error RuleSetNotFound =
        Error.NotFound("rules.rule_set_not_found", "No rule set with that identifier.");

    /// <summary>
    /// An active or archived set is what a trace from last quarter points at, so it
    /// is evidence rather than a document. Changing one would rewrite the
    /// explanation behind decisions already made and already acted on.
    /// </summary>
    public static readonly Error RuleSetNotEditable =
        Error.Conflict(
            "rules.rule_set_not_editable",
            "Only a draft rule set can be edited. Clone this one and edit the copy.");

    public static readonly Error RuleSetNotPromotable =
        Error.Conflict("rules.rule_set_not_promotable", "Only a draft rule set can be promoted.");

    public static readonly Error RuleSetEmpty =
        Error.Validation("rules.rule_set_empty", "A rule set with no rules would admit everything and rank nothing.");

    public static Error DuplicateRule(string code) =>
        Error.Validation("rules.duplicate_rule", $"Rule '{code}' appears more than once in the set.");

    public static Error RuleNotInVocabulary(string code, string kind) =>
        Error.Validation(
            "rules.rule_not_in_vocabulary",
            $"Rule '{code}' is not part of the {kind} vocabulary. A rule is typed against its own candidate and context.");

    /// <summary>
    /// A decision kind <c>DecisionKind</c> names but no vocabulary implements —
    /// waving and task assignment, as of Phase 6. Naming them in the enum before
    /// they have rules is deliberate: it is what makes the gap visible rather than
    /// forgotten.
    /// </summary>
    public static Error NoVocabulary(string kind) =>
        Error.Validation(
            "rules.no_vocabulary",
            $"{kind} decisions have no rule vocabulary in this build, so a rule set for them could never run.");

    public static Error RuleSetKindMismatch(string ruleSetKind, string requestedKind) =>
        Error.Validation(
            "rules.rule_set_kind_mismatch",
            $"That is a {ruleSetKind} rule set; the decisions asked about are {requestedKind}.");

    public static Error WeightOutOfRange(string code) =>
        Error.Validation("rules.weight_out_of_range", $"Rule '{code}' must carry a weight between 0 and 1.");
}
