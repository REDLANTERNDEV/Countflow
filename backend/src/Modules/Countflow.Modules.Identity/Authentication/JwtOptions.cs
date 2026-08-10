using System.ComponentModel.DataAnnotations;

namespace Countflow.Modules.Identity.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public required string Issuer { get; init; }

    [Required]
    public required string Audience { get; init; }

    /// <summary>Symmetric signing key. Must be at least 32 bytes for HS256.</summary>
    [Required]
    [MinLength(32)]
    public required string SigningKey { get; init; }

    /// <summary>
    /// Deliberately an hour rather than the usual 15 minutes. A handheld working
    /// through a WiFi dead zone replays its queued commands on reconnect, and a
    /// token that expired mid-queue turns a sync into a re-login at the worst
    /// moment. Rotation on the refresh token is what keeps this safe.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>A device stays enrolled across shifts; re-login should be rare.</summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
}
