namespace Countflow.Modules.Identity.Domain;

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
