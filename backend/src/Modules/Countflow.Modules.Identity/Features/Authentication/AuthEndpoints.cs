using System.Security.Claims;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Identity.Features.Authentication;

public static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth")
            .WithTags("Authentication");

        group.MapPost("/login", async (
                LoginRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            {
                var result = await authService.LoginAsync(request, cancellationToken);
                return result.ToHttpResult();
            })
            .AllowAnonymous()
            .WithName("Login")
            .WithSummary("Exchange credentials for an access token and a refresh token.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", async (
                RefreshRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            {
                var result = await authService.RefreshAsync(request.RefreshToken, cancellationToken);
                return result.ToHttpResult();
            })
            .AllowAnonymous()
            .WithName("Refresh")
            .WithSummary("Rotate a refresh token for a new access token.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", async (
                LogoutRequest request,
                AuthService authService,
                CancellationToken cancellationToken) =>
            {
                var result = await authService.LogoutAsync(request.RefreshToken, cancellationToken);
                return result.ToHttpResult();
            })
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("Revoke a refresh token.")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/me", async (
                ClaimsPrincipal principal,
                AuthService authService,
                CancellationToken cancellationToken) =>
            {
                var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(subject, out var userId))
                {
                    return Microsoft.AspNetCore.Http.Results.Unauthorized();
                }

                var result = await authService.GetUserInfoAsync(userId, cancellationToken);
                return result.ToHttpResult();
            })
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .WithSummary("Return the authenticated user and their roles.")
            .Produces<UserInfo>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
