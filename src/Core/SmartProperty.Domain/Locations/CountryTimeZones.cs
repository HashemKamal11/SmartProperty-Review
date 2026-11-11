namespace SmartProperty.Domain.Locations;

/// <summary>
/// The IANA time zone of a country that has exactly one, found from its ISO 3166-1 alpha-2 code.
/// </summary>
/// <remarks>
/// A rental's dates and check-in times are in the property's local time. A country with several zones (or one this
/// table does not know) has no time zone here, and a rental in it cannot be advertised until one is recorded, rather
/// than guessing a zone that might be an hour wrong.
/// </remarks>
public static class CountryTimeZones
{
    private static readonly Dictionary<string, string> ByCode = new(StringComparer.Ordinal)
    {
        ["EG"] = "Africa/Cairo",
        ["SA"] = "Asia/Riyadh",
        ["AE"] = "Asia/Dubai",
        ["KW"] = "Asia/Kuwait",
        ["QA"] = "Asia/Qatar",
        ["BH"] = "Asia/Bahrain",
        ["OM"] = "Asia/Muscat",
        ["JO"] = "Asia/Amman",
        ["LB"] = "Asia/Beirut",
        ["IQ"] = "Asia/Baghdad",
        ["LY"] = "Africa/Tripoli",
        ["TN"] = "Africa/Tunis",
        ["DZ"] = "Africa/Algiers",
        ["MA"] = "Africa/Casablanca",
    };

    /// <summary>The time zone identifier for an upper-case country code, or null when it has none recorded.</summary>
    public static string? FindByCountryCode(string? code)
    {
        return code is not null && ByCode.TryGetValue(code, out var zone) ? zone : null;
    }

    /// <summary>Every code with a recorded zone and its zone, for the migration that backfills existing countries.</summary>
    public static IReadOnlyDictionary<string, string> All => ByCode;
}
