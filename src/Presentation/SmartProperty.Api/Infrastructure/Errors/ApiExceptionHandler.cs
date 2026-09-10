#nullable enable
using Microsoft.AspNetCore.Diagnostics;

namespace SmartProperty.Api.Infrastructure.Errors;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        logger.LogError(exception, "Unhandled exception occurred.");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";

        var response = ApiErrorResponseFactory.UnexpectedFailure(httpContext);
        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }
}
