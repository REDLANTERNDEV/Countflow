using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Countflow.Api.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests per window for a caller presenting a credential.</summary>
    [Range(1, 1_000_000)]
    public int CredentialPermitsPerWindow { get; set; } = 600;

    /// <summary>
    /// Requests per window for a caller presenting none. Much tighter, because the
    /// endpoints reachable without a credential are the login endpoints, and the
    /// traffic that hammers them is not integration traffic.
    /// </summary>
    [Range(1, 1_000_000)]
    public int AnonymousPermitsPerWindow { get; set; } = 60;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Per-credential rate limiting for the published surface.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Set on the partition key so two callers cannot collide across kinds — an IP
    /// address that happens to read like a hash should not share a bucket with one.
    /// </summary>
    private const string CredentialPartition = "credential";

    private const string AddressPartition = "address";

    public static IServiceCollection AddCountflowRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations();

        var limits = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
            ?? new RateLimitingOptions();

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Health probes are exempt. Docker re-invokes the API binary against
                // /health/ready every few seconds, and a container that gets itself
                // killed and restarted for being too healthy is a poor trade.
                if (context.Request.Path.StartsWithSegments("/health"))
                {
                    return RateLimitPartition.GetNoLimiter("health");
                }

                var credential = Credential(context);

                return credential is null
                    ? RateLimitPartition.GetSlidingWindowLimiter(
                        $"{AddressPartition}:{context.Connection.RemoteIpAddress}",
                        _ => Window(limits.AnonymousPermitsPerWindow, limits.Window))
                    : RateLimitPartition.GetSlidingWindowLimiter(
                        $"{CredentialPartition}:{credential}",
                        _ => Window(limits.CredentialPermitsPerWindow, limits.Window));
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // Always a Retry-After, whether or not the limiter offered one. A
                // 429 without it tells a client to back off and not for how long,
                // which in practice means it retries immediately and stays refused;
                // the window is a correct upper bound when the lease has nothing
                // more precise to say.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var offered)
                    ? offered
                    : limits.Window;

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                // The same problem+json envelope with a `code` extension every other
                // failure in this API uses, so an integrator branches on 429 the way
                // they branch on everything else rather than special-casing it.
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        title = "Too many requests",
                        status = StatusCodes.Status429TooManyRequests,
                        detail = "This credential has made too many requests. Retry after the interval given.",
                        code = "rate_limit.exceeded"
                    },
                    cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Identifies the caller by the credential they presented, <b>without
    /// validating it</b>.
    /// <para>
    /// That is the whole design, and it is why the limiter runs before
    /// authentication in the pipeline. Partitioning on the authenticated identity
    /// would mean every request — including a flood of forged API keys from one
    /// script — pays for a database lookup <i>before</i> anything decides whether
    /// to serve it, which turns the credential check into the denial-of-service
    /// vector the whole hashing decision was made to avoid (see <c>ApiKey</c>).
    /// Partitioning on the presented bytes costs a SHA-256 of a header and
    /// throttles a forged credential exactly as hard as a real one.
    /// </para>
    /// <para>
    /// It is hashed rather than used directly because a partition key is a string
    /// held in memory, quoted in diagnostics and potentially logged, and a bucket
    /// keyed on a live secret is a place secrets accumulate for no benefit.
    /// </para>
    /// </summary>
    private static string? Credential(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            return Fingerprint(apiKey!);
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authorization)
            && !string.IsNullOrWhiteSpace(authorization))
        {
            return Fingerprint(authorization!);
        }

        return null;
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static SlidingWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,

        // Six segments: the window slides in ten-second steps for the default
        // minute. A fixed window would let a caller spend a whole minute's budget
        // in the last second of one window and again in the first second of the
        // next, which is the burst the limit exists to stop.
        SegmentsPerWindow = 6,

        // No queue. A warehouse client that is over its limit should be told so
        // immediately and back off; holding its request open makes a slow client
        // look like a slow API and consumes a connection while it waits.
        QueueLimit = 0
    };
}
