#nullable enable
namespace SmartProperty.Api.Infrastructure.Sharing;

/// <summary>
/// Where a shared property opens: <c>Share:PublicBaseUrl</c> (an absolute http(s) origin, e.g.
/// <c>https://app.example.com</c>) and <c>Share:PropertyPath</c> (default <c>/properties/{id}</c>). When no valid
/// base URL is configured, the share link falls back to the origin the request arrived on, so a full URL is always
/// returned. In Docker set it with <c>Share__PublicBaseUrl</c>.
/// </summary>
public sealed class ShareOptions
{
    public const string SectionName = "Share";
    public const string DefaultPropertyPath = "/properties/{id}";

    public string? PublicBaseUrl { get; init; }

    public string? PropertyPath { get; init; }

    public string BuildPath(Guid id)
    {
        var template = string.IsNullOrWhiteSpace(PropertyPath) ? DefaultPropertyPath : PropertyPath.Trim();
        if (!template.StartsWith('/'))
        {
            template = "/" + template;
        }

        return template.Replace("{id}", id.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The absolute URL: the configured base when valid, otherwise <paramref name="fallbackOrigin"/>.</summary>
    public string BuildUrl(Guid id, string fallbackOrigin)
    {
        return (ConfiguredOrigin() ?? fallbackOrigin.TrimEnd('/')) + BuildPath(id);
    }

    private string? ConfiguredOrigin()
    {
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)
            || !Uri.TryCreate(PublicBaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return null;
        }

        return baseUri.GetLeftPart(UriPartial.Authority);
    }
}
