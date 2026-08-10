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
}
