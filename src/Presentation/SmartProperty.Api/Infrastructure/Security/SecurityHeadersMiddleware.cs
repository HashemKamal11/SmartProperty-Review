#nullable enable
namespace SmartProperty.Api.Infrastructure.Security;

/// <summary>
/// Adds the response headers a JSON API should always send: no content sniffing, no framing, no referrer, and a
/// Content-Security-Policy that allows nothing, because no API response is a page.
/// </summary>
/// <remarks>
/// The interactive API reference (Development and Staging) is a page that needs scripts and styles, so it is left
/// without the policy. Headers a component set itself (the photo endpoint's own <c>nosniff</c>, a cache policy) are
/// not overwritten except for the ones that must be uniform.
/// </remarks>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private static readonly string[] DocumentationPrefixes = ["/scalar", "/openapi"];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";

            if (!IsDocumentation(context.Request.Path))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    private static bool IsDocumentation(PathString path) =>
        DocumentationPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
