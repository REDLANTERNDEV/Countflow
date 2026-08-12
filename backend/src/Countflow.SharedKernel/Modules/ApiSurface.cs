namespace Countflow.SharedKernel.Modules;

/// <summary>
/// Where the published HTTP surface lives, and what version of it this build is.
/// <para>
/// <b>The version belongs to the surface, not to an endpoint.</b> Modules map
/// relative paths — <c>/inventory/movements</c>, not
/// <c>/api/v1/inventory/movements</c> — and the host mounts every one of them
/// under this prefix in a single place. That is deliberate twice over. A route
/// literal written by an endpoint author cannot drift out of step with the version
/// the document advertises, because the author never writes it; and standing up a
/// v2 alongside v1 is a change to the host rather than an edit to fifty-odd files,
/// which is the difference between a version bump somebody will actually do and one
/// they will work around.
/// </para>
/// <para>
/// It is a whole-surface version rather than a per-endpoint or date-stamped one.
/// The heavier schemes exist to let a large API move in pieces; this one is small
/// enough that a single number a reader can hold in their head is worth more than
/// the granularity, and a compatibility policy in writing (see
/// <c>docs/api/README.md</c>) is what actually protects an integrator.
/// </para>
/// </summary>
public static class ApiSurface
{
    /// <summary>The current published major version.</summary>
    public const string Version = "v1";

    /// <summary>The prefix every module endpoint is mounted under.</summary>
    public const string Prefix = "/api/" + Version;
}
