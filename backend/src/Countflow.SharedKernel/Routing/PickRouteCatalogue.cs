namespace Countflow.SharedKernel.Routing;

/// <summary>
/// Every way of walking a zone that this build knows about.
/// <para>
/// The routing twin of <c>PutawayRuleCatalogue</c>, and it keeps that catalogue's
/// bargain: adding a strategy is a change to this repository, and a code nobody
/// registered is refused rather than skipped. It is refused at the moment somebody
/// configures a zone, though, and not at the moment a picker asks for work — see
/// <see cref="PickRoute.Plan"/> for why the two ends of that differ, and
/// <c>decision 15</c> for the argument.
/// </para>
/// </summary>
public static class PickRouteCatalogue
{
    public const string SequenceOrdinal = "route.sequence_ordinal";
    public const string SShape = "route.s_shape";
    public const string Return = "route.return";
    public const string Midpoint = "route.midpoint";

    /// <summary>
    /// What a zone is walked with until somebody says otherwise, and the only one
    /// that asks the building for nothing. A default that needed aisle data would
    /// make a fresh install unable to sequence anything, which is how a feature ends
    /// up switched off everywhere.
    /// </summary>
    public static readonly IPickRouteStrategy Fallback = new SequenceOrdinalRoute();

    public static IReadOnlyList<IPickRouteStrategy> All { get; } =
    [
        Fallback,
        new SShapeRoute(),
        new ReturnRoute(),
        new MidpointRoute()
    ];

    private static readonly Dictionary<string, IPickRouteStrategy> ByCode =
        All.ToDictionary(strategy => strategy.Code, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> Codes => ByCode.Keys;

    /// <summary>The strategy registered under that code, or null. Null is a configuration error, never a default.</summary>
    public static IPickRouteStrategy? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var strategy) ? strategy : null;

    public static bool Knows(string? code) => Find(code) is not null;
}
