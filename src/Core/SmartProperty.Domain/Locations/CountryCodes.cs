namespace SmartProperty.Domain.Locations;

/// <summary>
/// The ISO 3166-1 alpha-2 code of a country, found from the English name the AI search service gives it.
/// </summary>
/// <remarks>
/// The AI service lists countries by name only, and a property's address needs the code. A country this table does not
/// know is stored without a code and a property in it cannot be created until the code is set, rather than guessing.
/// </remarks>
public static class CountryCodes
{
    private static readonly Dictionary<string, string> ByEnglishName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Saudi Arabia"] = "SA",
        ["Kingdom of Saudi Arabia"] = "SA",
        ["Egypt"] = "EG",
        ["Arab Republic of Egypt"] = "EG",
        ["United Arab Emirates"] = "AE",
        ["UAE"] = "AE",
        ["Kuwait"] = "KW",
        ["Qatar"] = "QA",
        ["Bahrain"] = "BH",
        ["Oman"] = "OM",
        ["Jordan"] = "JO",
        ["Lebanon"] = "LB",
        ["Iraq"] = "IQ",
        ["Syria"] = "SY",
        ["Yemen"] = "YE",
        ["Palestine"] = "PS",
        ["Libya"] = "LY",
        ["Tunisia"] = "TN",
        ["Algeria"] = "DZ",
        ["Morocco"] = "MA",
        ["Sudan"] = "SD",
        ["Turkey"] = "TR",
        ["Cyprus"] = "CY",
        ["Greece"] = "GR",
        ["United Kingdom"] = "GB",
        ["United States"] = "US",
    };

    /// <summary>The code for an English country name, or null when the name is not known.</summary>
    public static string? FindByEnglishName(string? nameEn)
    {
        return nameEn is not null && ByEnglishName.TryGetValue(nameEn.Trim(), out var code) ? code : null;
    }

    /// <summary>The code in its stored form (two upper-case ASCII letters), or null for no code.</summary>
    /// <exception cref="ArgumentException">The code is present but is not two ASCII letters.</exception>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();

        if (trimmed.Length != 2 || !char.IsAsciiLetter(trimmed[0]) || !char.IsAsciiLetter(trimmed[1]))
        {
            throw new ArgumentException("A country code must be exactly two ASCII letters.", nameof(code));
        }

        return trimmed.ToUpperInvariant();
    }
}
