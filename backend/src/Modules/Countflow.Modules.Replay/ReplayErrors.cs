using Countflow.SharedKernel.Results;

namespace Countflow.Modules.Replay;

/// <summary>
/// The few ways running an experiment can fail on its own account. Everything a
/// replay can fail at for a <i>rules</i> reason — an unknown rule code, a rule set
/// of the wrong kind, a warehouse with no locations — comes back from
/// <c>IShadowReplay</c> already coded, and is passed through rather than
/// re-described here.
/// </summary>
public static class ReplayErrors
{
    public static readonly Error WindowInverted =
        Error.Validation("replay.window_inverted", "The window ends before it starts.");

    public static readonly Error RunNotFound =
        Error.NotFound("replay.run_not_found", "No replay run with that identifier.");

    public static readonly Error WarehouseNotFound =
        Error.NotFound("replay.warehouse_not_found", "No warehouse with that code.");

    public static readonly Error UnknownActor =
        Error.Unauthorized("replay.unknown_actor", "The access token carries no usable user identity.");
}
