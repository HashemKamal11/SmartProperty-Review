#nullable enable
using Microsoft.Extensions.Options;

namespace SmartProperty.Api.Infrastructure.Cors;

/// <summary>
/// Browser origins permitted to call the API through the named frontend CORS policy.
/// </summary>
internal sealed class FrontendCorsOptions
{
    public const string SectionName = "Cors";

    public string[]? AllowedOrigins { get; init; } = [];

    public bool HasOnlyValidOrigins()
    {
        return TryGetNormalizedAllowedOrigins(out _, out _);
    }

    /// <summary>
    /// Removes harmless padding, blank entries, and duplicates while rejecting values that are not exact HTTP(S)
    /// origins. Validation is performed while the host is built so malformed deployment configuration is visible
    /// immediately instead of silently denying the intended frontend.
    /// </summary>
    public string[] GetNormalizedAllowedOrigins()
    {
        if (TryGetNormalizedAllowedOrigins(out var normalizedOrigins, out var invalidOrigin))
        {
            return normalizedOrigins;
        }

        throw new OptionsValidationException(
            SectionName,
            typeof(FrontendCorsOptions),
            [$"{SectionName}:AllowedOrigins contains invalid browser origin '{invalidOrigin}'. " +
             "Use an exact http:// or https:// origin without wildcards, credentials, paths, queries, " +
             "fragments, or a trailing slash."]);
    }

    private bool TryGetNormalizedAllowedOrigins(
        out string[] normalizedOrigins,
        out string? invalidOrigin)
    {
        var collectedOrigins = new List<string>();
        var seenOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        invalidOrigin = null;

        foreach (var configuredOrigin in AllowedOrigins ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredOrigin))
            {
                continue;
            }

            var trimmedOrigin = configuredOrigin.Trim();
            if (!TryNormalizeOrigin(trimmedOrigin, out var normalizedOrigin))
            {
                invalidOrigin = trimmedOrigin;
                normalizedOrigins = [];
                return false;
            }

            if (seenOrigins.Add(normalizedOrigin))
            {
                collectedOrigins.Add(normalizedOrigin);
            }
        }

        normalizedOrigins = [.. collectedOrigins];
        return true;
    }

    private static bool TryNormalizeOrigin(string origin, out string normalizedOrigin)
    {
        normalizedOrigin = string.Empty;

        if (origin.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var schemeSeparator = origin.IndexOf("://", StringComparison.Ordinal);
        var authority = schemeSeparator >= 0 ? origin[(schemeSeparator + 3)..] : string.Empty;
        if (authority.Length == 0
            || authority.Contains('/', StringComparison.Ordinal)
            || authority.Contains('?', StringComparison.Ordinal)
            || authority.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        normalizedOrigin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
