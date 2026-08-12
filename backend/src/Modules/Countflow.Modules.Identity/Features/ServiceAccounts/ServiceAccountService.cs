using System.Security.Cryptography;
using Countflow.Modules.Identity.Authentication;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Persistence;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Identity.Features.ServiceAccounts;

/// <summary>
/// Creates integrations and the credentials they authenticate with.
/// </summary>
public sealed class ServiceAccountService(
    UserManager<CountflowUser> userManager,
    IdentityModuleDbContext dbContext,
    TimeProvider clock,
    ILogger<ServiceAccountService> logger)
{
    public static readonly Error NotAServiceAccount = Error.Validation(
        "identity.not_a_service_account",
        "API keys belong to service accounts. This account signs in with a password.");

    private static readonly Error AccountNotFound =
        Error.NotFound("identity.service_account_not_found", "No such service account.");

    private static readonly Error KeyNotFound =
        Error.NotFound("identity.api_key_not_found", "No such API key.");

    private static readonly Error NameTaken =
        Error.Conflict("identity.account_name_taken", "An account with that name already exists.");

    private static readonly Error UnknownRole =
        Error.Validation("identity.unknown_role", "That role does not exist.");

    private static readonly Error NoRoles =
        Error.Validation("identity.no_roles", "A service account with no roles could not do anything.");

    public async Task<Result<ServiceAccountResponse>> CreateAsync(
        CreateServiceAccountRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Result.Failure<ServiceAccountResponse>(Error.Validation(
                "identity.name_required", "A service account needs a name and a display name."));
        }

        if (request.Roles.Count == 0)
        {
            return Result.Failure<ServiceAccountResponse>(NoRoles);
        }

        var roles = request.Roles.Select(role => role.Trim()).Distinct(StringComparer.Ordinal).ToList();

        if (roles.Any(role => !Roles.All.Contains(role, StringComparer.Ordinal)))
        {
            return Result.Failure<ServiceAccountResponse>(UnknownRole);
        }

        if (await userManager.FindByNameAsync(name) is not null)
        {
            return Result.Failure<ServiceAccountResponse>(NameTaken);
        }

        var account = new CountflowUser
        {
            UserName = name,
            // Synthesised rather than asked for. An integration has no inbox, and a
            // nullable email would mean every read of a user has to cope with one.
            Email = $"{name}@service.countflow.local",
            EmailConfirmed = true,
            DisplayName = request.DisplayName.Trim(),

            // Operator codes are unique and an integration will never scan a badge,
            // so it gets one that is obviously not a badge.
            //
            // Random rather than a slice of a UUIDv7. That is what this was, and it
            // collided immediately: a v7's leading hex digits are its millisecond
            // timestamp, so two accounts created in the same millisecond get the
            // same prefix and the second one dies on the unique index. Sortable
            // identifiers are exactly the wrong thing to truncate for uniqueness.
            OperatorCode = $"SVC-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}",
            IsServiceAccount = true
        };

        // Deliberately CreateAsync *without* a password. Half of the invariant is
        // enforced by refusal in AuthService; this is the other half, and it is the
        // stronger one — there is no hash to check, so no future change to the login
        // path can accidentally let a machine credential in through the front door.
        var created = await userManager.CreateAsync(account);
        if (!created.Succeeded)
        {
            return Result.Failure<ServiceAccountResponse>(Error.Validation(
                "identity.account_rejected",
                string.Join("; ", created.Errors.Select(error => error.Description))));
        }

        await userManager.AddToRolesAsync(account, roles);

        logger.LogInformation(
            "Created service account {Name} ({AccountId}) with roles {Roles}", name, account.Id, roles);

        return new ServiceAccountResponse(
            account.Id, name, account.DisplayName, roles, account.IsActive, account.CreatedAt, ActiveKeyCount: 0);
    }

    public async Task<Result<IReadOnlyList<ServiceAccountResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var accounts = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsServiceAccount)
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return Result.Success<IReadOnlyList<ServiceAccountResponse>>([]);
        }

        var ids = accounts.Select(account => account.Id).ToList();

        var rolesByAccount = await dbContext.UserRoles
            .AsNoTracking()
            .Where(link => ids.Contains(link.UserId))
            .Join(dbContext.Roles, link => link.RoleId, role => role.Id, (link, role) => new { link.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var activeKeys = await dbContext.ApiKeys
            .AsNoTracking()
            .Where(key => ids.Contains(key.UserId)
                && key.RevokedAt == null
                && (key.ExpiresAt == null || key.ExpiresAt > now))
            .GroupBy(key => key.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.UserId, row => row.Count, cancellationToken);

        return Result.Success<IReadOnlyList<ServiceAccountResponse>>(
        [
            .. accounts.Select(account => new ServiceAccountResponse(
                account.Id,
                account.UserName ?? string.Empty,
                account.DisplayName,
                [.. rolesByAccount.Where(row => row.UserId == account.Id).Select(row => row.Name ?? string.Empty)],
                account.IsActive,
                account.CreatedAt,
                activeKeys.GetValueOrDefault(account.Id)))
        ]);
    }

    public async Task<Result<CreatedApiKeyResponse>> IssueKeyAsync(
        Guid accountId,
        CreateApiKeyRequest request,
        Guid issuedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Result.Failure<CreatedApiKeyResponse>(Error.Validation(
                "identity.key_name_required", "Give the key a name, so somebody can tell later what it is for."));
        }

        var account = await dbContext.Users
            .SingleOrDefaultAsync(user => user.Id == accountId, cancellationToken);

        if (account is null)
        {
            return Result.Failure<CreatedApiKeyResponse>(AccountNotFound);
        }

        // The second half of "a human cannot hold a key". A human account is not
        // *missing* — saying so would be a lie an admin would waste time on — it is
        // the wrong kind of principal, and the error says which.
        if (!account.IsServiceAccount)
        {
            return Result.Failure<CreatedApiKeyResponse>(NotAServiceAccount);
        }

        if (!account.IsActive)
        {
            return Result.Failure<CreatedApiKeyResponse>(Error.Conflict(
                "identity.account_inactive", "That service account is disabled."));
        }

        var secret = ApiKeySecret.Create();
        var now = clock.GetUtcNow();

        var key = new ApiKey
        {
            UserId = account.Id,
            Name = request.Name.Trim(),
            SecretHash = secret.Hash,
            Prefix = secret.Prefix,
            CreatedAt = now,
            CreatedByUserId = issuedBy,
            ExpiresAt = request.ExpiresInDays is { } days and > 0 ? now.AddDays(days) : null
        };

        dbContext.ApiKeys.Add(key);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Issued API key {Prefix} for service account {AccountId} by {IssuedBy}",
            secret.Prefix, account.Id, issuedBy);

        return new CreatedApiKeyResponse(Describe(key), secret.Secret);
    }

    public async Task<Result<IReadOnlyList<ApiKeyResponse>>> ListKeysAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == accountId, cancellationToken);

        if (account is null || !account.IsServiceAccount)
        {
            return Result.Failure<IReadOnlyList<ApiKeyResponse>>(AccountNotFound);
        }

        // Revoked keys are listed too. They are the record of what this integration
        // used to be able to do, and a list that hid them would make an expired
        // credential in a log line unattributable.
        var keys = await dbContext.ApiKeys
            .AsNoTracking()
            .Where(key => key.UserId == accountId)
            .OrderByDescending(key => key.CreatedAt)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<ApiKeyResponse>>([.. keys.Select(Describe)]);
    }

    public async Task<Result<ApiKeyResponse>> RevokeKeyAsync(
        Guid keyId,
        Guid revokedBy,
        CancellationToken cancellationToken)
    {
        var key = await dbContext.ApiKeys.SingleOrDefaultAsync(row => row.Id == keyId, cancellationToken);

        if (key is null)
        {
            return Result.Failure<ApiKeyResponse>(KeyNotFound);
        }

        // Never a delete. Revoking twice is not an error — an integration being
        // urgently cut off is a thing two people do at once.
        if (key.RevokedAt is null)
        {
            key.RevokedAt = clock.GetUtcNow();
            key.RevokedByUserId = revokedBy;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning("Revoked API key {Prefix} ({KeyId}) by {RevokedBy}", key.Prefix, key.Id, revokedBy);
        }

        return Describe(key);
    }

    private static ApiKeyResponse Describe(ApiKey key) => new(
        key.Id,
        key.UserId,
        key.Name,
        key.Prefix,
        key.CreatedAt,
        key.ExpiresAt,
        key.LastUsedAt,
        key.RevokedAt);
}
