using System.Text.RegularExpressions;

namespace SmartProperty.Domain.Identity;

/// <summary>
/// International phone numbers in E.164 form: '+', a country code that does not start with 0, and at most 15 digits
/// in total. Spaces, dashes, dots, and parentheses are accepted on input and removed. The number is not checked
/// against a country's numbering plan; ownership is proved by the verification code instead.
/// </summary>
public static partial class PhoneNumberFormat
{
    public const int MaxLength = 16;

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = new string(value.Where(character => !IsSeparator(character)).ToArray());

        if (!E164().IsMatch(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static string Normalize(string value)
    {
        return TryNormalize(value, out var normalized)
            ? normalized
            : throw new ArgumentException("Phone number must be in international format, for example +201012345678.", nameof(value));
    }

    private static bool IsSeparator(char character)
    {
        return char.IsWhiteSpace(character) || character is '-' or '.' or '(' or ')';
    }
}
