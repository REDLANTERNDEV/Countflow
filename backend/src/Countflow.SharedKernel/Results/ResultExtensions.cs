using Microsoft.AspNetCore.Http;

namespace Countflow.SharedKernel.Results;

public static class ResultExtensions
{
    /// <summary>
    /// Maps a domain <see cref="Result"/> onto an HTTP response, so error
    /// translation lives in one place instead of at every endpoint.
    /// </summary>
    public static IResult ToHttpResult<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(result.Value) : Problem(result.Error);

    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : Problem(result.Error);

    private static IResult Problem(Error error) =>
        Microsoft.AspNetCore.Http.Results.Problem(
            title: TitleFor(error.Type),
            detail: error.Description,
            statusCode: StatusFor(error.Type),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.Unauthorized => "Unauthorized",
        ErrorType.Forbidden => "Forbidden",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        _ => "An unexpected error occurred"
    };
}
