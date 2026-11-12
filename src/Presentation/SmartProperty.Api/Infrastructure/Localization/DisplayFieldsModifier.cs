#nullable enable
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SmartProperty.Common.Localization;

namespace SmartProperty.Api.Infrastructure.Localization;

/// <summary>
/// Adds a read-only <c>display&lt;Field&gt;</c> property, in the request's language, next to the fields of every response
/// object of this platform that clients would otherwise translate themselves:
/// <list type="bullet">
/// <item>an English/Arabic pair <c>xEn</c> + <c>xAr</c>, or <c>x</c> + <c>xAr</c>: <c>displayX</c> is the one in the
/// request's language (<c>nameEn</c>/<c>nameAr</c> give <c>displayName</c>);</item>
/// <item>a closed-list value (a string or enum whose name ends in Status, Type, Kind, Purpose, Role, Period, Decision,
/// Outcome, State, Step, Level, Scope, Stage, Requirement, Verdict or Capacity, a string list such as
/// <c>purposes</c> or <c>missing</c>, or anything marked <see cref="LocalizedValueAttribute"/>): its label from
/// <see cref="DisplayValues"/>;</item>
/// <item>a message marked <see cref="LocalizedMessageAttribute"/>, such as the error body's <c>message</c>: its
/// translation from <see cref="DisplayMessages"/>.</item>
/// </list>
/// The original fields are unchanged, so existing clients keep working; requests ignore the added fields.
/// </summary>
public static class DisplayFieldsModifier
{
    public const string Prefix = "display";

    private static readonly string[] ValueSuffixes =
    [
        "Status", "Type", "Kind", "Purpose", "Role", "Period", "Decision", "Outcome", "State", "Step", "Level", "Scope",
        "Stage", "Requirement", "Verdict", "Capacity"
    ];

    private static readonly string[] ListNames = ["Purposes", "Missing", "Types", "Statuses", "Kinds", "Roles"];

    private static readonly HashSet<string> NotValues = new(StringComparer.Ordinal) { "ContentType", "MimeType", "MediaType" };

    /// <summary>Adds the modifier to the resolver of <paramref name="options"/>.</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(Modify);
    }

    public static void Modify(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (typeInfo.Kind != JsonTypeInfoKind.Object
            || typeInfo.Type.Namespace?.StartsWith("SmartProperty.", StringComparison.Ordinal) != true)
        {
            return;
        }

        var originals = typeInfo.Properties.ToArray();
        var byClrName = new Dictionary<string, JsonPropertyInfo>(StringComparer.Ordinal);
        foreach (var property in originals)
        {
            if (ClrName(property) is { } name)
            {
                byClrName.TryAdd(name, property);
            }
        }

        var taken = new HashSet<string>(originals.Select(property => property.Name), StringComparer.Ordinal);
        var paired = new HashSet<string>(StringComparer.Ordinal);
        var additions = new List<(JsonPropertyInfo After, JsonPropertyInfo Display)>();

        // English/Arabic pairs first: a field that has its own Arabic version is shown from it, not from a label.
        foreach (var arabic in originals)
        {
            if (ClrName(arabic) is not { } arabicName
                || !arabicName.EndsWith("Ar", StringComparison.Ordinal)
                || arabicName.Length <= 2
                || !IsText(arabic))
            {
                continue;
            }

            var baseName = arabicName[..^2];
            var english = byClrName.GetValueOrDefault(baseName + "En") ?? byClrName.GetValueOrDefault(baseName);
            if (english is null || !IsText(english) || english.Get is null || arabic.Get is null)
            {
                continue;
            }

            var displayName = DisplayName(english.Name.EndsWith("En", StringComparison.Ordinal) && english.Name.Length > 2
                ? english.Name[..^2]
                : english.Name);
            if (!taken.Add(displayName))
            {
                continue;
            }

            paired.Add(ClrName(english)!);
            var getEnglish = english.Get;
            var getArabic = arabic.Get;
            var display = typeInfo.CreateJsonPropertyInfo(typeof(string), displayName);
            display.Get = owner => DisplayLanguage.Pick(getEnglish(owner) as string, getArabic(owner) as string);
            SkipWhenNull(display, english, arabic);
            additions.Add((Array.IndexOf(originals, english) > Array.IndexOf(originals, arabic) ? english : arabic, display));
        }

        foreach (var property in originals)
        {
            if (ClrName(property) is not { } clrName || paired.Contains(clrName) || property.Get is null || HasAttribute<NotLocalizedAttribute>(property))
            {
                continue;
            }

            var getter = property.Get;
            JsonPropertyInfo? display = null;
            var displayName = DisplayName(property.Name);

            if (HasAttribute<LocalizedMessageAttribute>(property) && IsText(property))
            {
                display = typeInfo.CreateJsonPropertyInfo(typeof(string), displayName);
                display.Get = owner => DisplayMessages.Display(getter(owner) as string);
            }
            else if (IsValueList(property, clrName))
            {
                display = typeInfo.CreateJsonPropertyInfo(typeof(IReadOnlyList<string?>), displayName);
                display.Get = owner => getter(owner) is IEnumerable values
                    ? values.Cast<object?>().Select(value => DisplayValues.Display(value?.ToString())).ToArray()
                    : null;
            }
            else if (IsValue(property, clrName))
            {
                display = typeInfo.CreateJsonPropertyInfo(typeof(string), displayName);
                display.Get = owner => DisplayValues.Display(getter(owner)?.ToString());
            }

            if (display is null || !taken.Add(displayName))
            {
                continue;
            }

            SkipWhenNull(display, property);
            additions.Add((property, display));
        }

        // Each display field goes right after the field it shows, so the response reads naturally.
        foreach (var (after, display) in additions)
        {
            typeInfo.Properties.Insert(typeInfo.Properties.IndexOf(after) + 1, display);
        }
    }

    private static string DisplayName(string jsonName) =>
        jsonName.Length == 0 ? Prefix : Prefix + char.ToUpperInvariant(jsonName[0]) + jsonName[1..];

    private static bool IsValue(JsonPropertyInfo property, string clrName)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type != typeof(string) && !type.IsEnum)
        {
            return false;
        }

        if (HasAttribute<LocalizedValueAttribute>(property))
        {
            return true;
        }

        return !NotValues.Contains(clrName)
            && ValueSuffixes.Any(suffix => clrName.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static bool IsValueList(JsonPropertyInfo property, string clrName)
    {
        var type = property.PropertyType;
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        var element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        element = element is null ? null : Nullable.GetUnderlyingType(element) ?? element;

        return element is not null
            && (element == typeof(string) || element.IsEnum)
            && (HasAttribute<LocalizedValueAttribute>(property) || ListNames.Any(name => clrName.EndsWith(name, StringComparison.Ordinal)));
    }

    private static bool IsText(JsonPropertyInfo property) => property.PropertyType == typeof(string);

    private static string? ClrName(JsonPropertyInfo property) => (property.AttributeProvider as MemberInfo)?.Name;

    private static bool HasAttribute<TAttribute>(JsonPropertyInfo property)
        where TAttribute : Attribute =>
        property.AttributeProvider?.IsDefined(typeof(TAttribute), inherit: true) == true;

    /// <summary>A display field is left out exactly when the fields it is made from are left out for being null.</summary>
    private static void SkipWhenNull(JsonPropertyInfo display, params JsonPropertyInfo[] sources)
    {
        var omitsNull = sources.Any(source => source.AttributeProvider?
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .Any(ignore => ignore.Condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault) == true);

        if (omitsNull)
        {
            display.ShouldSerialize = (_, value) => value is not null;
        }
    }
}
