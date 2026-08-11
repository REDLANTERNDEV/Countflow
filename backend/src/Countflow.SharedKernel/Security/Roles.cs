namespace Countflow.SharedKernel.Security;

/// <summary>
/// The authorisation vocabulary of the whole system.
/// <para>
/// In SharedKernel rather than in the Identity module, even though Identity is
/// what stores them. Every module gates its own endpoints — inventory
/// adjustments are a supervisor's call, releasing a wave is not an operator's —
/// so the role names are a cross-cutting contract, not Identity's private data.
/// A module reaching into <c>Countflow.Modules.Identity</c> for a string constant
/// would be exactly the boundary violation ADR 0002 forbids, and hardcoding
/// <c>"Supervisor"</c> in five modules is how one of them ends up spelled wrong.
/// </para>
/// </summary>
public static class Roles
{
    /// <summary>Configures the system: master data, rule sets, users.</summary>
    public const string Admin = "Admin";

    /// <summary>Runs the floor: releases waves, approves count variances, reads dashboards.</summary>
    public const string Supervisor = "Supervisor";

    /// <summary>Executes directed work on a handheld. The largest group by far.</summary>
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = [Admin, Supervisor, Operator];
}
