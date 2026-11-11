using System.Globalization;

namespace SmartProperty.Domain.OwnerVerification;

/// <summary>
/// The countries whose identity documents an owner may use, and the format check for each. These are structural
/// checks only (length, fixed prefixes, the birth date an Egyptian ID encodes); check digits are not verified and
/// nothing is confirmed with an issuing authority. The reviewer compares the document itself.
/// </summary>
public static class NationalIdRules
{
    /// <summary>Saudi Arabia: national ID (starts with 1) or Iqama (starts with 2), 10 digits.</summary>
    public const string SaudiArabia = "SA";

    /// <summary>Egypt: national ID, 14 digits; the first seven encode the century and the date of birth.</summary>
    public const string Egypt = "EG";

    /// <summary>United Arab Emirates: Emirates ID, 15 digits starting with 784.</summary>
    public const string UnitedArabEmirates = "AE";

    public const int CountryCodeLength = 2;
    public const int MaxNationalIdLength = 15;

    public static IReadOnlyList<string> SupportedCountries { get; } = [SaudiArabia, Egypt, UnitedArabEmirates];

    /// <summary>The upper-case, trimmed code when it is supported; otherwise null.</summary>
    public static string? NormalizeCountry(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        var normalized = countryCode.Trim().ToUpperInvariant();

        return SupportedCountries.Contains(normalized) ? normalized : null;
    }

    /// <summary>The ID without spaces or hyphens (as printed on Emirates IDs, for example), or null when empty.</summary>
    public static string? NormalizeNationalId(string? nationalId)
    {
        if (string.IsNullOrWhiteSpace(nationalId))
        {
            return null;
        }

        return string.Concat(nationalId.Where(character => character is not (' ' or '-')));
    }

    /// <summary>
    /// Null when <paramref name="nationalId"/> (already normalized) is well formed for <paramref name="countryCode"/>
    /// (already normalized), otherwise the reason it is not. <paramref name="birthDate"/>, when given, must match the
    /// date an Egyptian ID encodes.
    /// </summary>
    public static string? Validate(string countryCode, string? nationalId, DateOnly? birthDate)
    {
        return countryCode switch
        {
            SaudiArabia => IsDigits(nationalId, 10) && nationalId![0] is '1' or '2'
                ? null
                : "A Saudi national ID or Iqama must be 10 digits starting with 1 (citizen) or 2 (resident).",
            Egypt => ValidateEgyptian(nationalId, birthDate),
            UnitedArabEmirates => IsDigits(nationalId, 15) && nationalId!.StartsWith("784", StringComparison.Ordinal)
                ? null
                : "An Emirates ID must be 15 digits starting with 784.",
            _ => "The ID country is not supported."
        };
    }

    public static bool IsValid(string countryCode, string? nationalId) => Validate(countryCode, nationalId, null) is null;

    private static string? ValidateEgyptian(string? nationalId, DateOnly? birthDate)
    {
        const string format = "An Egyptian national ID must be 14 digits starting with 2 (born 1900-1999) or 3 (born 2000-2099).";

        if (!IsDigits(nationalId, 14) || nationalId![0] is not ('2' or '3'))
        {
            return format;
        }

        var century = nationalId[0] == '2' ? 1900 : 2000;
        var year = century + int.Parse(nationalId.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(nationalId.AsSpan(3, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(nationalId.AsSpan(5, 2), CultureInfo.InvariantCulture);

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return "The date of birth in the Egyptian national ID (digits 2 to 7) is not a valid date.";
        }

        if (birthDate is { } expected && expected != new DateOnly(year, month, day))
        {
            return "The birth date does not match the date of birth in the Egyptian national ID (digits 2 to 7).";
        }

        return null;
    }

    private static bool IsDigits(string? value, int length)
    {
        return value is not null && value.Length == length && value.All(char.IsAsciiDigit);
    }
}
