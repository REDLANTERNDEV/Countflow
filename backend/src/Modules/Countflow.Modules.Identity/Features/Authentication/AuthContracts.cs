namespace Countflow.Modules.Identity.Features.Authentication;

/// <param name="Identifier">Username, email, or the operator code from a badge.</param>
public sealed record LoginRequest(string Identifier, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

/// <param name="IsServiceAccount">
/// True when this principal is an integration. <c>GET /auth/me</c> is the smoke
/// test an integrator runs first, and it should answer both halves of "did my
/// credential work": who am I, and what am I allowed to do.
/// </param>
public sealed record UserInfo(
    Guid Id,
    string UserName,
    string DisplayName,
    string OperatorCode,
    IReadOnlyList<string> Roles,
    bool IsServiceAccount = false);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    UserInfo User);
