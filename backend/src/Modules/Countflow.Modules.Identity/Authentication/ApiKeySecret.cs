using System.Security.Cryptography;

namespace Countflow.Modules.Identity.Authentication;

/// <summary>
/// A freshly minted machine credential: the secret to hand back exactly once, and
/// the two things about it worth keeping.
/// </summary>
/// <param name="Secret">The full value the integration sends in <c>X-Api-Key</c>. Never persisted.</param>
/// <param name="Hash">SHA-256 of <paramref name="Secret"/>, hex-encoded. This is what is stored.</param>
/// <param name="Prefix">The leading, non-secret portion, stored so a person can identify the key later.</param>
public sealed record ApiKeySecret(string Secret, string Hash, string Prefix)
{
    /// <summary>
    /// Marks the string as ours wherever it turns up — a log line, a paste into an
    /// issue, a secret scanner's ruleset. A credential nobody recognises is a
    /// credential nobody reports.
    /// </summary>
    public const string Scheme = "cfk";

    /// <summary>
    /// How much of the value is quoted back in listings. Enough to tell two keys
    /// apart at a glance, far short of enough to attack: the remaining 28 base64url
    /// characters still carry ~168 bits.
    /// </summary>
    private const int PrefixRandomChars = 6;

    /// <summary>Bits of entropy in the random half. This is why one SHA-256 is sufficient — see <c>ApiKey</c>.</summary>
    private const int SecretBytes = 32;

    public static ApiKeySecret Create()
    {
        // Base64url rather than hex: the same 256 bits in 43 characters instead of
        // 64, and no padding or escaping to go wrong in a header, a shell variable
        // or a YAML file.
        var random = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var secret = $"{Scheme}_{random}";

        return new ApiKeySecret(secret, TokenService.Hash(secret), $"{Scheme}_{random[..PrefixRandomChars]}");
    }

    /// <summary>
    /// Describes a secret that was supplied rather than generated — the demo stack
    /// takes its key from the environment so a person can point a client at it, and
    /// a key that was minted through the API can never be recovered to do that with.
    /// The caller is responsible for the value being random; nothing here can check
    /// that, which is why this is not the path the API offers.
    /// </summary>
    public static ApiKeySecret Describe(string secret)
    {
        var trimmed = secret.Trim();
        var randomPart = trimmed.StartsWith($"{Scheme}_", StringComparison.Ordinal)
            ? trimmed[(Scheme.Length + 1)..]
            : trimmed;

        var prefix = randomPart.Length >= PrefixRandomChars
            ? $"{Scheme}_{randomPart[..PrefixRandomChars]}"
            : $"{Scheme}_supplied";

        return new ApiKeySecret(trimmed, TokenService.Hash(trimmed), prefix);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
