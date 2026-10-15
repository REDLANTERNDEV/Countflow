namespace Countflow.SharedKernel.Routing;

/// <summary>
/// A named way of walking a set of bins in one zone.
/// <para>
/// <b>A strategy is code, and the selection of one is data.</b> That is
/// <c>decision 4</c>'s split — a rule's configuration moves, its vocabulary does not
/// — arriving at routing for the same reason it arrived at labels in
/// <c>decision 13</c>. What makes it code here is sharper than resemblance: S-Shape,
/// Return and Midpoint are <i>mutually exclusive algorithms</i>, not weights. A rule
/// set combines its rules by weighting them, and a container that can express
/// <c>0.6 × S-Shape + 0.4 × Midpoint</c> can express something that is not a route.
/// The wrong container is the one whose combination operator means nothing.
/// </para>
/// <para>
/// <b>And a route is not a sort.</b> S-Shape is a function of the <i>set</i>: with
/// picks in aisles 1, 2 and 3 the third aisle is entered from the back, and with
/// picks in 1 and 3 the skipped aisle flips the parity and the third is entered from
/// the front. Aisle 3's direction changed without aisle 3 changing, so no key
/// computed from a single location can produce it — which is why this takes the
/// whole list, and why <see cref="Contracts.RouteScale"/>, a scalar projection,
/// cannot be the thing that sequences a pick list however much it is the thing that
/// ranks a zone.
/// </para>
/// </summary>
public interface IPickRouteStrategy
{
    /// <summary>
    /// The stable code held on the zone. Renaming one silently changes how a hall
    /// is walked, so it is treated the way a rule code is: written down, never
    /// derived from the class name.
    /// </summary>
    string Code { get; }

    /// <summary>What the building has to know about itself for this to mean anything.</summary>
    RouteGeometryRequirement Requires { get; }

    /// <summary>
    /// Orders the stops. Total by construction: a stop whose geometry is missing is
    /// placed rather than dropped, because a pick list that quietly lost a line is
    /// an order that quietly ships short.
    /// </summary>
    IReadOnlyList<RouteStop> Sequence(IReadOnlyList<RouteStop> stops, Guid zoneId, RouteGeometry geometry);
}
