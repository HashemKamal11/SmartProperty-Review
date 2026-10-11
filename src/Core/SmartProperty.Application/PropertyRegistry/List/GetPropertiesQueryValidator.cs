using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.List;

internal static class GetPropertiesQueryValidator
{
    /// <summary>
    /// Validates the country filter and hands back its normalized form: null when absent, otherwise the trimmed,
    /// upper-cased two-letter code the database stores.
    /// </summary>
    public static Error? Validate(GetPropertiesQuery query, out string? countryCode)
    {
        countryCode = null;

        if (string.IsNullOrWhiteSpace(query.CountryCode))
        {
            return null;
        }

        var trimmed = query.CountryCode.Trim();
        if (trimmed.Length != 2
            || !char.IsAsciiLetter(trimmed[0])
            || !char.IsAsciiLetter(trimmed[1]))
        {
            return GetPropertiesErrors.ValidationFailed("Country code must contain exactly two ASCII letters.");
        }

        countryCode = trimmed.ToUpperInvariant();

        return null;
    }
}
