#nullable enable
using System.Globalization;

namespace SmartProperty.Api.Infrastructure.Localization;

/// <summary>
/// The language a response is displayed in: English or Arabic. It is chosen per request by
/// <c>?lang=ar|en</c> or, failing that, the <c>Accept-Language</c> header, and defaults to English. Only the UI culture
/// changes; number and date formatting stay invariant, so nothing the API parses or formats behaves differently.
/// </summary>
public static class DisplayLanguage
{
    public const string English = "en";
    public const string Arabic = "ar";

    /// <summary>The query-string key that overrides <c>Accept-Language</c>: <c>?lang=ar</c>.</summary>
    public const string QueryKey = "lang";

    public static IReadOnlyList<string> Supported { get; } = [English, Arabic];

    public static bool IsArabic =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, Arabic, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>ar</c> or <c>en</c>.</summary>
    public static string Current => IsArabic ? Arabic : English;

    /// <summary>The text in the current language, or the other one when that is missing.</summary>
    public static string? Pick(string? english, string? arabic)
    {
        var preferred = IsArabic ? arabic : english;
        var other = IsArabic ? english : arabic;

        return string.IsNullOrWhiteSpace(preferred) ? (string.IsNullOrWhiteSpace(other) ? preferred ?? other : other) : preferred;
    }
}
