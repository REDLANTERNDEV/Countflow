namespace Countflow.Modules.Identity.Domain;

/// <summary>
/// A machine credential belonging to a service account.
/// <para>
/// Only the hash is persisted, exactly as for <see cref="RefreshToken"/>, so a
/// database leak hands out no usable credentials. The hash is a <b>single
/// SHA-256</b> rather than a password KDF, and that is a deliberate choice rather
/// than an oversight: the secret is 256 bits of CSPRNG output, so there is no
/// dictionary to slow an attacker down, and a work-factored KDF on the
/// authentication path of every request would be a denial-of-service vector an
/// unauthenticated caller could pull on us for the price of one HTTP request.
/// Passwords are the opposite case in both respects, which is why Identity still
/// hashes those through ASP.NET Core Identity's own hasher.
/// </para>
/// <para>
/// A key is <b>never deleted</b>, only revoked. The movements it posted name the
/// service account that made them, and a credential row that vanishes takes the
/// answer to "what was this integration allowed to do in March" with it.
/// </para>
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The service account this key authenticates as. Never a human.</summary>
    public required Guid UserId { get; init; }

    /// <summary>What the key is for, in a person's words. Shown wherever the key is listed.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 of the presented secret, hex-encoded. The secret itself is never stored.</summary>
    public required string SecretHash { get; init; }

    /// <summary>
    /// The leading, non-secret portion of the key, kept so a person can tell two
    /// keys apart in a list and match one against a log line without holding the
    /// secret. It identifies; it does not authenticate.
    /// </summary>
    public required string Prefix { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The human who minted it. A key nobody can be asked about is a key nobody revokes.</summary>
    public required Guid CreatedByUserId { get; init; }

    /// <summary>Null means it does not expire. An integration that runs forever is allowed to say so.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Coarse: written at most once every few minutes rather than on every request
    /// (see <c>ApiKeyAuthenticationHandler</c>). It answers "is this key still in
    /// use, or can it be revoked", which does not need second-level resolution and
    /// is not worth a write on the hot path of every authenticated call.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || now < ExpiresAt);
}
