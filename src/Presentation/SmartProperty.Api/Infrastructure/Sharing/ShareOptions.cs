#nullable enable
namespace SmartProperty.Api.Infrastructure.Sharing;

/// <summary>
/// Where a shared property opens. The API does not know the front end's address, so it is configured:
/// <c>Share:PublicBaseUrl</c> (an absolute http(s) origin, e.g. <c>https://app.example.com</c>) and
/// <c>Share:PropertyPath</c> (default <c>/properties/{id}</c>). Without a valid base URL the share response carries
/// only the relative path and no absolute URL.
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

    public string? BuildUrl(Guid id)
    {
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)
            || !Uri.TryCreate(PublicBaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return null;
        }

        return baseUri.GetLeftPart(UriPartial.Authority) + BuildPath(id);
    }
}
