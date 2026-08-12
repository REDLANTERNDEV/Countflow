using Microsoft.AspNetCore.Identity;

namespace Countflow.Modules.Identity.Domain;

public sealed class CountflowUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }

    /// <summary>
    /// Short code the operator keys or scans from their badge on the handheld.
    /// Typing a full email on a scanner keypad at shift start is miserable.
    /// </summary>
    public required string OperatorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// True when this principal is an integration rather than a person.
    /// <para>
    /// The invariant this flag exists to hold is that <b>a credential belongs to
    /// exactly one kind of principal</b>. A service account cannot log in with a
    /// password — <c>AuthService.LoginAsync</c> refuses it before it ever reaches
    /// the hasher, and the account is created with no password hash at all — and a
    /// human cannot hold an <see cref="ApiKey"/>, because minting one against a
    /// user without this flag is refused.
    /// </para>
    /// <para>
    /// One flag rather than two account tables, because everything downstream —
    /// roles, the movements it posts, the tasks it completes, the audit trail —
    /// wants an integration to be a first-class actor with a name, not a special
    /// case. What differs is only how it proves who it is.
    /// </para>
    /// </summary>
    public bool IsServiceAccount { get; set; }
}
