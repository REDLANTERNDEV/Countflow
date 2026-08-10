namespace Countflow.Modules.Identity.Domain;

/// <summary>
/// A rotating refresh token. Only the hash is persisted, so a database leak
/// does not hand out usable sessions.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    /// <summary>SHA-256 of the opaque token value. The value itself is never stored.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when this token was rotated, forming an audit chain.</summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
