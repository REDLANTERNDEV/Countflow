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
}
