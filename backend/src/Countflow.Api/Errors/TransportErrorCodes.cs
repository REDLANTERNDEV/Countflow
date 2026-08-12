using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Countflow.Api.Errors;

/// <summary>
/// Puts a <c>code</c> on the problem documents the framework produces for itself.
/// <para>
/// A domain failure goes through <c>Result.ToHttpResult()</c> and carries the
/// coded <c>Error</c> that caused it. But the most common failures an integrator
/// meets never reach a handler at all: a missing credential, a role they do not
/// hold, a mistyped path, a body that would not bind. Those are written by the
/// authorization middleware and by <c>UseStatusCodePages</c>, which know nothing
/// about our <c>Error</c> type, and they were coming out as bare RFC 9457
/// documents with a title and a status and nothing to branch on.
/// </para>
/// <para>
/// That was found by driving the surface by hand in Phase 7, and it mattered
/// because <c>docs/api/</c> tells an integrator to branch on <c>code</c> and never
/// on prose. A contract that holds for every failure <i>except the first one you
/// will hit</i> is worse than no contract: it is one that teaches the wrong lesson
/// on the happy path and breaks on a Friday.
/// </para>
/// <para>
/// Hooked into <c>CustomizeProblemDetails</c> rather than into a status-code-pages
/// callback, because that is the single point every problem document in the host
/// passes through — the exception handler, the status code pages, and
/// <c>Results.Problem</c> itself. An existing <c>code</c> is never overwritten, so
/// a domain error keeps the one it chose.
/// </para>
/// </summary>
public static class TransportErrorCodes
{
    public const string Extension = "code";

    public static void Apply(ProblemDetailsContext context)
    {
        if (context.ProblemDetails.Extensions.ContainsKey(Extension))
        {
            return;
        }

        var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;

        context.ProblemDetails.Extensions[Extension] = status switch
        {
            // The two an integrator meets first, and the distinction between them
            // is the one worth getting right: "you did not identify yourself"
            // versus "I know who you are and it is not enough". Retrying with the
            // same credential fixes neither, but only one of them is fixed by
            // getting a new one.
            StatusCodes.Status401Unauthorized => "auth.credential_required",
            StatusCodes.Status403Forbidden => "auth.role_required",

            StatusCodes.Status404NotFound => "http.not_found",
            StatusCodes.Status405MethodNotAllowed => "http.method_not_allowed",
            StatusCodes.Status406NotAcceptable => "http.not_acceptable",
            StatusCodes.Status415UnsupportedMediaType => "http.unsupported_media_type",
            StatusCodes.Status400BadRequest => "http.malformed_request",
            StatusCodes.Status408RequestTimeout => "http.request_timeout",
            StatusCodes.Status413PayloadTooLarge => "http.payload_too_large",
            StatusCodes.Status429TooManyRequests => "rate_limit.exceeded",
            _ => "http.unexpected_error"
        };

        // The trace identifier is already added by the framework's default
        // customiser when one is available; this only fills the gap where a
        // problem document was produced outside a tracked activity.
        context.ProblemDetails.Extensions.TryAdd(
            "traceId",
            System.Diagnostics.Activity.Current?.Id
                ?? context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity.Id);
    }
}
