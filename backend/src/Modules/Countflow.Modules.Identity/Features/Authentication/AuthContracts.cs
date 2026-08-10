namespace Countflow.Modules.Identity.Features.Authentication;

/// <param name="Identifier">Username, email, or the operator code from a badge.</param>
public sealed record LoginRequest(string Identifier, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record UserInfo(
    Guid Id,
    string UserName,
    string DisplayName,
    string OperatorCode,
    IReadOnlyList<string> Roles);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    UserInfo User);
