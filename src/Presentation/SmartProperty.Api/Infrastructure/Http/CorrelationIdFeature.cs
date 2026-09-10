#nullable enable
namespace SmartProperty.Api.Infrastructure.Http;

internal static class CorrelationIdFeature
{
    public const string HeaderName = "X-Correlation-ID";

    private const string ItemKey = "SmartProperty.CorrelationId";

    public static string Get(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Items.TryGetValue(ItemKey, out var value) && value is string correlationId
            ? correlationId
            : httpContext.TraceIdentifier;
    }

    public static void Set(HttpContext httpContext, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Items[ItemKey] = correlationId;
        httpContext.TraceIdentifier = correlationId;
    }
}
