#nullable enable
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;

namespace SmartProperty.Api.Infrastructure.Http;

internal sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    private const int MaxCorrelationIdLength = 128;

    public async Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var correlationId = GetRequestCorrelationId(httpContext.Request.Headers, out var requestCorrelationId)
            ? requestCorrelationId
            : httpContext.TraceIdentifier;

        CorrelationIdFeature.Set(httpContext, correlationId);

        httpContext.Response.OnStarting(() =>
        {
            httpContext.Response.Headers[CorrelationIdFeature.HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await next(httpContext);
        }
    }

    private static bool GetRequestCorrelationId(
        IHeaderDictionary headers,
        [NotNullWhen(true)] out string? correlationId)
    {
        correlationId = null;

        if (!headers.TryGetValue(CorrelationIdFeature.HeaderName, out StringValues values))
        {
            return false;
        }

        var value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length > MaxCorrelationIdLength || value.Any(char.IsControl))
        {
            return false;
        }

        correlationId = value;
        return true;
    }
}
