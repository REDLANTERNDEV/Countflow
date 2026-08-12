using System.Security.Claims;
using System.Text.Encodings.Web;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Countflow.Modules.Identity.Authentication;

/// <summary>
/// The names of the three authentication schemes this host runs, and the header
/// a machine credential arrives in.
/// </summary>
public static class CountflowAuthenticationSchemes
{
    /// <summary>
    /// The default scheme: a policy scheme that forwards to one of the two real
    /// ones on the presence of <see cref="ApiKeyHeader"/>. Nothing authenticates
    /// against it directly.
    /// </summary>
    public const string Selector = "Countflow";

    /// <summary>Machine credentials. See <see cref="ApiKeyAuthenticationHandler"/>.</summary>
    public const string ApiKey = "ApiKey";

    public const string ApiKeyHeader = "X-Api-Key";
}

/// <summary>
/// Authenticates a service account from its <c>X-Api-Key</c> header.
/// <para>
/// The whole point of doing it as an <see cref="AuthenticationHandler{T}"/> rather
/// than as middleware or a filter is that it produces the <i>same shape of
/// principal</i> the JWT handler does — the subject under
/// <see cref="ClaimTypes.NameIdentifier"/>, the account name under
/// <see cref="ClaimTypes.Name"/>, and one <see cref="ClaimTypes.Role"/> per role.
/// Every <c>RequireRole</c> already written on every endpoint therefore applies to
/// an integration unchanged, and <b>no second authorization vocabulary exists to
/// drift out of step with the first</b>. Scopes beside roles would be two sets of
/// rules about the same question, and the second set is always the one nobody
/// updates.
/// </para>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentityModuleDbContext dbContext,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// How stale <see cref="ApiKey.LastUsedAt"/> is allowed to get before a request
    /// pays for a write. "Is anyone still using this key?" is a question about days;
    /// a write on every authenticated request to answer it to the second would
    /// double the cost of authentication and produce a hot row per integration.
    /// </summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(CountflowAuthenticationSchemes.ApiKeyHeader, out var header))
        {
            // NoResult, not Fail: the forwarder only sends us requests carrying the
            // header, so arriving here without one means something else should have
            // had a look.
            return AuthenticateResult.NoResult();
        }

        var presented = header.ToString().Trim();
        if (string.IsNullOrEmpty(presented))
        {
            return AuthenticateResult.Fail("The X-Api-Key header is empty.");
        }

        var hash = TokenService.Hash(presented);
        var now = clock.GetUtcNow();

        // One indexed lookup on the hash. There is deliberately no prefix lookup
        // followed by a comparison: the hash is the whole credential, so matching
        // on it directly is both simpler and constant-time in the database.
        var found = await dbContext.ApiKeys
            .Where(key => key.SecretHash == hash)
            .Join(
                dbContext.Users,
                key => key.UserId,
                user => user.Id,
                (key, user) => new { Key = key, User = user })
            .SingleOrDefaultAsync(Context.RequestAborted);

        if (found is null)
        {
            return AuthenticateResult.Fail("The API key is not recognised.");
        }

        if (!found.Key.IsActive(now))
        {
            return AuthenticateResult.Fail("The API key has been revoked or has expired.");
        }

        // Both halves of the invariant are checked here, not only on minting: a
        // user could in principle have been converted after a key was issued, and
        // a credential that outlived the account shape it belongs to is exactly the
        // thing this flag exists to prevent.
        if (!found.User.IsActive || !found.User.IsServiceAccount)
        {
            return AuthenticateResult.Fail("The account this key belongs to cannot authenticate.");
        }

        await TouchAsync(found.Key, now);

        var roles = await dbContext.UserRoles
            .Where(link => link.UserId == found.User.Id)
            .Join(dbContext.Roles, link => link.RoleId, role => role.Id, (_, role) => role.Name)
            .ToListAsync(Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, found.User.Id.ToString()),
            new(ClaimTypes.Name, found.User.UserName ?? string.Empty),
            new(CountflowClaims.DisplayName, found.User.DisplayName),
            new(CountflowClaims.OperatorCode, found.User.OperatorCode),

            // Which credential, not merely which account. A service account may
            // hold several keys, and "revoke the one the nightly job uses" needs
            // the log line to say which one was presented.
            new(CountflowClaims.ApiKeyId, found.Key.Id.ToString()),
            new(CountflowClaims.ApiKeyPrefix, found.Key.Prefix)
        };

        claims.AddRange(roles.Where(role => role is not null).Select(role => new Claim(ClaimTypes.Role, role!)));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CountflowAuthenticationSchemes.ApiKey, ClaimTypes.Name, ClaimTypes.Role));

        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, CountflowAuthenticationSchemes.ApiKey));
    }

    private async Task TouchAsync(ApiKey key, DateTimeOffset now)
    {
        if (key.LastUsedAt is { } last && now - last < LastUsedResolution)
        {
            return;
        }

        // ExecuteUpdate rather than a tracked save: the entity came out of a join
        // projection, this is the only column changing, and a concurrent request
        // writing the same value a moment later is not a conflict worth resolving.
        await dbContext.ApiKeys
            .Where(row => row.Id == key.Id)
            .ExecuteUpdateAsync(row => row.SetProperty(x => x.LastUsedAt, now), Context.RequestAborted);
    }
}
