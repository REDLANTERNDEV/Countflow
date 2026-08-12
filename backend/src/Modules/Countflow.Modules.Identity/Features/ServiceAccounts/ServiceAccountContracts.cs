namespace Countflow.Modules.Identity.Features.ServiceAccounts;

/// <param name="Roles">
/// The same role names a person holds. There is no separate scope vocabulary, on
/// purpose: an integration that may release a wave is a Supervisor, and one that
/// may promote a rule set is an Admin, judged by the identical
/// <c>RequireRole</c> the console is judged by.
/// </param>
public sealed record CreateServiceAccountRequest(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Roles);

public sealed record ServiceAccountResponse(
    Guid Id,
    string Name,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTimeOffset CreatedAt,
    int ActiveKeyCount);

/// <param name="ExpiresInDays">Null means the key does not expire.</param>
public sealed record CreateApiKeyRequest(string Name, int? ExpiresInDays = null);

public sealed record ApiKeyResponse(
    Guid Id,
    Guid ServiceAccountId,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// The one response in the system that carries a secret, and the only time this
/// one is ever produced. There is no endpoint that reads a key back: the hash is
/// all that is stored, so "show it to me again" is not a feature that was left
/// out — it is unimplementable, which is the property that makes the hash worth
/// having.
/// </summary>
/// <param name="Secret">Send this in the <c>X-Api-Key</c> header. It is not recoverable.</param>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, string Secret);
