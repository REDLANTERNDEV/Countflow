using System.Security.Claims;
using Countflow.Modules.Identity.Authentication;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Identity.Features.ServiceAccounts;

/// <summary>
/// Managing integrations and their credentials. Admin only, and human-only —
/// see <see cref="RequireHuman"/>.
/// </summary>
public static class ServiceAccountEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/service-accounts")
            .WithTags("Service accounts")
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin));

        group.MapPost("/", async (
                CreateServiceAccountRequest request,
                ClaimsPrincipal principal,
                ServiceAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                if (RequireHuman(principal) is { } refusal)
                {
                    return refusal;
                }

                var created = await accounts.CreateAsync(request, cancellationToken);
                return created.ToHttpResult();
            })
            .WithName("CreateServiceAccount")
            .WithSummary("Create an integration account that authenticates with an API key.")
            .Produces<ServiceAccountResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (
                ServiceAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var listed = await accounts.ListAsync(cancellationToken);
                return listed.ToHttpResult();
            })
            .WithName("GetServiceAccounts")
            .WithSummary("List the integration accounts and how many live keys each holds.")
            .Produces<IReadOnlyList<ServiceAccountResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/keys", async (
                Guid id,
                CreateApiKeyRequest request,
                ClaimsPrincipal principal,
                ServiceAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                if (RequireHuman(principal) is { } refusal)
                {
                    return refusal;
                }

                if (Subject(principal) is not { } issuedBy)
                {
                    return Microsoft.AspNetCore.Http.Results.Unauthorized();
                }

                var issued = await accounts.IssueKeyAsync(id, request, issuedBy, cancellationToken);
                return issued.ToHttpResult();
            })
            .WithName("CreateApiKey")
            .WithSummary("Mint an API key. The secret is returned once and cannot be read back.")
            .Produces<CreatedApiKeyResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/keys", async (
                Guid id,
                ServiceAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var keys = await accounts.ListKeysAsync(id, cancellationToken);
                return keys.ToHttpResult();
            })
            .WithName("GetApiKeys")
            .WithSummary("List an account's keys, live and revoked. Never their secrets.")
            .Produces<IReadOnlyList<ApiKeyResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/keys/{keyId:guid}/revoke", async (
                Guid keyId,
                ClaimsPrincipal principal,
                ServiceAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                if (Subject(principal) is not { } revokedBy)
                {
                    return Microsoft.AspNetCore.Http.Results.Unauthorized();
                }

                // Deliberately *not* behind RequireHuman. Everything else here mints
                // authority; this one only ever removes it, and an integration that
                // notices its own credential has leaked should be able to burn it at
                // three in the morning without waiting for somebody to log in.
                var revoked = await accounts.RevokeKeyAsync(keyId, revokedBy, cancellationToken);
                return revoked.ToHttpResult();
            })
            .WithName("RevokeApiKey")
            .WithSummary("Revoke a key. Keys are never deleted — the audit trail points at them.")
            .Produces<ApiKeyResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Refuses a request that arrived on a machine credential.
    /// <para>
    /// This is not a second authorization vocabulary — the role check above is
    /// still the only thing deciding <i>what</i> may be done. It is the credential
    /// invariant one floor up: a leaked key must not be able to mint a fresh,
    /// permanent key for itself and so survive the revocation of the one that
    /// leaked. Cutting an integration off has to actually cut it off.
    /// </para>
    /// </summary>
    private static IResult? RequireHuman(ClaimsPrincipal principal) =>
        principal.Identity?.AuthenticationType == CountflowAuthenticationSchemes.ApiKey
            ? Result.Failure(Error.Forbidden(
                "identity.human_credential_required",
                "Issuing credentials is done by a signed-in administrator, not by an API key.")).ToHttpResult()
            : null;

    private static Guid? Subject(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
