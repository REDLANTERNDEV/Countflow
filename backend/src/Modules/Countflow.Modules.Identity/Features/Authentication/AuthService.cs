using Countflow.Modules.Identity.Authentication;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Persistence;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Countflow.Modules.Identity.Features.Authentication;

public sealed class AuthService(
    UserManager<CountflowUser> userManager,
    IdentityModuleDbContext dbContext,
    TokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    ILogger<AuthService> logger)
{
    private static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "The credentials provided are not valid.");

    private static readonly Error InvalidRefreshToken =
        Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is not valid.");

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(request.Identifier, cancellationToken);

        // Same error whether the user is missing or the password is wrong, so the
        // response cannot be used to enumerate valid accounts.
        if (user is null || !user.IsActive)
        {
            return Result.Failure<AuthResponse>(InvalidCredentials);
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            logger.LogWarning("Failed login attempt for user {UserId}", user.Id);
            return Result.Failure<AuthResponse>(InvalidCredentials);
        }

        return await IssueAsync(user, cancellationToken);
    }

    public async Task<Result<AuthResponse>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = TokenService.Hash(refreshToken);

        var stored = await dbContext.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return Result.Failure<AuthResponse>(InvalidRefreshToken);
        }

        // A token we already rotated away is being presented again. Either it was
        // stolen, or a client replayed it — in both cases the whole chain is
        // suspect, so every active session for this user is revoked.
        if (!stored.IsActive)
        {
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoking all active sessions", stored.UserId);

            await RevokeAllForUserAsync(stored.UserId, cancellationToken);
            return Result.Failure<AuthResponse>(InvalidRefreshToken);
        }

        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        if (user is null || !user.IsActive)
        {
            return Result.Failure<AuthResponse>(InvalidRefreshToken);
        }

        var issued = await IssueAsync(user, cancellationToken, rotating: stored);
        return issued;
    }

    public async Task<Result> LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = TokenService.Hash(refreshToken);

        var stored = await dbContext.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Logging out with an unknown or already-revoked token is not an error.
        return Result.Success();
    }

    public async Task<Result<UserInfo>> GetUserInfoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure<UserInfo>(Error.NotFound("auth.user_not_found", "The user no longer exists."));
        }

        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(ToUserInfo(user, roles));
    }

    private async Task<CountflowUser?> FindUserAsync(string identifier, CancellationToken cancellationToken)
    {
        var normalized = identifier.Trim();

        // Operator code first: it is what gets scanned or keyed on the floor.
        var user = await dbContext.Users
            .SingleOrDefaultAsync(u => u.OperatorCode == normalized, cancellationToken);

        user ??= await userManager.FindByNameAsync(normalized);
        user ??= await userManager.FindByEmailAsync(normalized);

        return user;
    }

    private async Task<Result<AuthResponse>> IssueAsync(
        CountflowUser user,
        CancellationToken cancellationToken,
        RefreshToken? rotating = null)
    {
        var roles = await userManager.GetRolesAsync(user);
        var (accessToken, expiresAt) = tokenService.CreateAccessToken(user, roles);

        var refreshToken = TokenService.CreateRefreshToken();
        var refreshHash = TokenService.Hash(refreshToken);

        if (rotating is not null)
        {
            rotating.RevokedAt = DateTimeOffset.UtcNow;
            rotating.ReplacedByTokenHash = refreshHash;
        }

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = DateTimeOffset.UtcNow.Add(jwtOptions.Value.RefreshTokenLifetime)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new AuthResponse(accessToken, expiresAt, refreshToken, ToUserInfo(user, roles)));
    }

    private async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), cancellationToken);

    private static UserInfo ToUserInfo(CountflowUser user, IEnumerable<string> roles) =>
        new(user.Id, user.UserName ?? string.Empty, user.DisplayName, user.OperatorCode, [.. roles]);
}
