using Countflow.Modules.Identity.Authentication;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Countflow.Api.OpenApi;

/// <summary>
/// The parts of the published contract that are about the surface as a whole
/// rather than about any one endpoint.
/// </summary>
public static class OpenApiDocumentTransformers
{
    /// <summary>
    /// Fills in <c>info</c>. Without this the document is titled
    /// "Countflow.Api | v1" after the assembly, which tells a reader the name of
    /// our build output and nothing about what they are integrating with.
    /// </summary>
    public static Task DescribeTheSurface(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Countflow",
            Version = ApiSurface.Version,
            Description = $"""
                The warehouse management surface a Countflow deployment publishes. Every
                path below is mounted under `{ApiSurface.Prefix}`; there is no unversioned
                alias, and no private surface behind this one — the console and the
                handheld are clients of exactly these endpoints.

                **Authenticating.** Two credentials, one set of permissions. A person
                exchanges a password at `POST {ApiSurface.Prefix}/auth/login` for a bearer
                token; an integration sends a service account's key in the
                `{CountflowAuthenticationSchemes.ApiKeyHeader}` header. Both produce the
                same principal, and every endpoint's role requirement applies to both
                identically. There is no scope vocabulary to learn.

                **Writing safely.** Every command that changes stock carries a caller-supplied
                idempotency key. Retry a write that timed out with the same key and it posts
                once; reuse a key for a different command and it is refused. Handhelds on a
                warehouse wireless link depend on this, and so should you.

                **Following along.** `GET {ApiSurface.Prefix}/inventory/changes` walks the
                stock ledger forwards from a cursor and hands out every change exactly once,
                in order. It is the intended way to keep an external system in step.

                See `docs/api/` in the repository for the compatibility policy, the error
                code table, and both warehouse loops end to end with worked requests.
                """,
            License = new OpenApiLicense { Name = "MIT" }
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Declares both credentials as security schemes, and makes the bearer token
    /// the document-level default.
    /// <para>
    /// The default matters more than it looks. Almost every endpoint requires
    /// authentication, and a handful — login, refresh, logout — do not; minimal
    /// APIs already annotate the anonymous ones, so declaring the requirement once
    /// at the document and letting those override it describes the surface
    /// correctly with no per-endpoint bookkeeping to fall out of date.
    /// </para>
    /// </summary>
    public static Task DescribeSecurity(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "A person's access token, from POST /auth/login. Expires; rotate it with "
                + "POST /auth/refresh. Service accounts cannot obtain one."
        };

        document.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = CountflowAuthenticationSchemes.ApiKeyHeader,
            In = ParameterLocation.Header,
            Description =
                "A service account's machine credential. Issued once by an administrator "
                + "and never readable afterwards; revocable at any time. Carries the roles "
                + "its account holds, which are the same roles a person holds."
        };

        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("bearerAuth", document)] = [],
            },
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("apiKey", document)] = [],
            }
        ];

        return Task.CompletedTask;
    }
}
