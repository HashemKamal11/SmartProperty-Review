#nullable enable
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartProperty.Api.Infrastructure.Localization;

/// <summary>
/// Arabic versions of the platform's English messages (errors and validation failures), read from the embedded
/// <c>ArabicMessages.json</c>. A key is either the exact English text or an English template with the same
/// <c>{placeholders}</c> as the C# interpolated string that produces it; the values a placeholder matched are put back into
/// the Arabic text. A message with no translation is shown in English.
/// </summary>
public static class DisplayMessages
{
    private const string ResourceName = "SmartProperty.Api.Infrastructure.Localization.ArabicMessages.json";

    private static readonly Lazy<Catalog> Arabic = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The message in the current language.</summary>
    public static string? Display(string? message)
    {
        if (message is null || !DisplayLanguage.IsArabic)
        {
            return message;
        }

        return Arabic.Value.Translate(message) ?? message;
    }

    /// <summary>The Arabic version of <paramref name="message"/>, or null when there is none.</summary>
    public static string? ToArabic(string message) => Arabic.Value.Translate(message);

    /// <summary>How many English messages have an Arabic version.</summary>
    public static int Count => Arabic.Value.Count;

    private static Catalog Load()
    {
        using var stream = typeof(DisplayMessages).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is empty.");

        return new Catalog(entries);
    }

    private sealed class Catalog
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

        private readonly FrozenDictionary<string, string> _exact;
        private readonly Template[] _templates;

        public Catalog(IReadOnlyDictionary<string, string> entries)
        {
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var templates = new List<Template>();

            foreach (var (english, arabic) in entries)
            {
                var parsed = Template.TryParse(english, arabic);
                if (parsed is null)
                {
                    exact[english] = arabic;
                }
                else
                {
                    templates.Add(parsed);
                }
            }

            _exact = exact.ToFrozenDictionary(StringComparer.Ordinal);

            // The template with the most fixed text is the most specific, so it is tried first.
            _templates = [.. templates.OrderByDescending(template => template.LiteralLength)];
            Count = entries.Count;
        }

        public int Count { get; }

        public string? Translate(string message) => Translate(message, depth: 0);

        private string? Translate(string message, int depth)
        {
            if (_exact.TryGetValue(message, out var arabic))
            {
                return arabic;
            }

            foreach (var template in _templates)
            {
                var translated = template.TryTranslate(message, value => TranslateValue(value, depth + 1));
                if (translated is not null)
                {
                    return translated;
                }
            }

            return null;
        }

        /// <summary>
        /// What a placeholder matched, in Arabic where possible: a nested message ("...: the property is not available."),
        /// or closed-list values ("Details, OwnershipProof"). Anything else (codes, numbers, names) is kept as it is.
        /// </summary>
        private string TranslateValue(string value, int depth)
        {
            if (depth <= 2 && Translate(value, depth) is { } message)
            {
                return message;
            }

            var parts = value.Split(", ", StringSplitOptions.None);
            return parts.All(part => part.Length > 0 && DisplayValues.IsKnown(part))
                ? string.Join("، ", parts.Select(DisplayValues.Display))
                : value;
        }

        private sealed class Template
        {
            private readonly Regex _pattern;
            private readonly string _arabic;
            private readonly string[] _placeholders;

            private Template(Regex pattern, string arabic, string[] placeholders, int literalLength)
            {
                _pattern = pattern;
                _arabic = arabic;
                _placeholders = placeholders;
                LiteralLength = literalLength;
            }

            public int LiteralLength { get; }

            /// <summary>Null when <paramref name="english"/> has no placeholder, so it is matched exactly.</summary>
            public static Template? TryParse(string english, string arabic)
            {
                var pattern = new StringBuilder("^");
                var placeholders = new List<string>();
                var literalLength = 0;
                var index = 0;

                while (index < english.Length)
                {
                    var character = english[index];
                    if (character == '{' && index + 1 < english.Length && english[index + 1] == '{')
                    {
                        pattern.Append(Regex.Escape("{"));
                        literalLength++;
                        index += 2;
                        continue;
                    }

                    if (character == '}' && index + 1 < english.Length && english[index + 1] == '}')
                    {
                        pattern.Append(Regex.Escape("}"));
                        literalLength++;
                        index += 2;
                        continue;
                    }

                    if (character == '{')
                    {
                        var end = english.IndexOf('}', index + 1);
                        if (end < 0)
                        {
                            return null;
                        }

                        var placeholder = english[index..(end + 1)];
                        var group = placeholders.IndexOf(placeholder);
                        if (group < 0)
                        {
                            placeholders.Add(placeholder);
                            pattern.Append("(?<p").Append(placeholders.Count - 1).Append(">.+?)");
                        }
                        else
                        {
                            pattern.Append("\\k<p").Append(group).Append('>');
                        }

                        index = end + 1;
                        continue;
                    }

                    pattern.Append(Regex.Escape(character.ToString()));
                    literalLength++;
                    index++;
                }

                if (placeholders.Count == 0)
                {
                    return null;
                }

                pattern.Append('$');

                return new Template(
                    new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout),
                    arabic.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal),
                    [.. placeholders],
                    literalLength);
            }

            public string? TryTranslate(string message, Func<string, string> translateValue)
            {
                Match match;
                try
                {
                    match = _pattern.Match(message);
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }

                if (!match.Success)
                {
                    return null;
                }

                var result = _arabic;
                for (var group = 0; group < _placeholders.Length; group++)
                {
                    result = result.Replace(_placeholders[group], translateValue(match.Groups["p" + group].Value), StringComparison.Ordinal);
                }

                return result;
            }
        }
    }
}
