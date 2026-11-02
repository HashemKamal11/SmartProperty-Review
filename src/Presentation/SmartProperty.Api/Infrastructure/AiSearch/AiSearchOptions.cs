#nullable enable
namespace SmartProperty.Api.Infrastructure.AiSearch;

/// <summary>
/// Settings for the AI search service, bound from the "AiSearch" section. ApiKey is a secret: supply it through
/// environment variables (AiSearch__ApiKey), never source control.
/// </summary>
/// <remarks>
/// Off unless <see cref="Enabled"/> is true, so a developer or a test host never writes listings into a shared AI
/// service by accident. While off, nothing is sent and every property reports itself as not synced.
/// </remarks>
internal sealed class AiSearchOptions
{
    public const string SectionName = "AiSearch";

    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = "https://ai-search.neurix.uk";

    /// <summary>Sent as <c>x-api-key</c> when adding or removing listings. Searching needs no key.</summary>
    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 30;

    public bool IsValid()
    {
        return !Enabled
            || (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && TimeoutSeconds is >= 1 and <= 300);
    }
}
