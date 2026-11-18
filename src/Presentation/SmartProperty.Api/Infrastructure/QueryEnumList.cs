#nullable enable
namespace SmartProperty.Api.Infrastructure;

/// <summary>
/// Reads a multi-value enum query parameter. The framework's enum binder turns "Villa,Apartment" into the bitwise OR
/// of the two numbers, which is a third, unrelated member, so such parameters are bound as text and parsed here.
/// </summary>
internal static class QueryEnumList
{
    /// <summary>
    /// Parses repeated and/or comma-separated values, each a defined name (any case) or number. False when any value
    /// is not one defined member. Null (no restriction) when nothing was sent.
    /// </summary>
    public static bool TryParse<TEnum>(string[]? values, out IReadOnlyList<TEnum>? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;

        if (values is null || values.Length == 0)
        {
            return true;
        }

        var result = new List<TEnum>();

        foreach (var part in values.SelectMany(value => (value ?? "").Split(',')).Select(part => part.Trim()))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (!Enum.TryParse<TEnum>(part, ignoreCase: true, out var member) || !Enum.IsDefined(member))
            {
                return false;
            }

            result.Add(member);
        }

        parsed = result.Count == 0 ? null : result;

        return true;
    }
}
