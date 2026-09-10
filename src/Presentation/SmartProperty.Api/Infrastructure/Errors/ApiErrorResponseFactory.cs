#nullable enable
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Infrastructure.Errors;

internal static class ApiErrorResponseFactory
{
    public static ApiErrorResponse FromError(Error error, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(httpContext);

        var status = error.Type switch
        {
            ErrorType.Failure => StatusCodes.Status400BadRequest,
            ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => throw new ArgumentOutOfRangeException(
                nameof(error.Type),
                error.Type,
                "Unsupported error type.")
        };

        return new ApiErrorResponse(
            error.Code,
            error.Description,
            status,
            FieldErrors: null,
            GetCorrelationId(httpContext));
    }

    public static ApiErrorResponse MalformedRequest(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new ApiErrorResponse(
            ApiErrorCodes.MalformedRequest,
            "The request is malformed.",
            StatusCodes.Status400BadRequest,
            FieldErrors: null,
            GetCorrelationId(httpContext));
    }

    public static ApiErrorResponse UnexpectedFailure(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new ApiErrorResponse(
            ApiErrorCodes.UnexpectedFailure,
            "An unexpected server error occurred.",
            StatusCodes.Status500InternalServerError,
            FieldErrors: null,
            GetCorrelationId(httpContext));
    }

    private static string GetCorrelationId(HttpContext httpContext)
    {
        return CorrelationIdFeature.Get(httpContext);
    }
}
