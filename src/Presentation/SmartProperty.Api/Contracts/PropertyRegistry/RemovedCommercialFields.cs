#nullable enable
using System.Text.Json;

namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// The commercial and advertisement members that used to be part of a property and now belong to a marketplace
/// listing.
/// </summary>
/// <remarks>
/// A request that still names one of them is refused instead of ignored: the property endpoints replace the whole
/// state, so silently dropping a price or a title would lose data without the client noticing. Matching is
/// case-insensitive, like model binding. Any other unknown member keeps its existing behavior and is ignored.
/// </remarks>
internal static class RemovedCommercialFields
{
    private static readonly string[] Names =
    [
        "transactionType",
        "price",
        "listingStatus",
        "titleAr",
        "titleEn",
        "descriptionAr",
        "descriptionEn",
    ];

    /// <summary>The removed members the body named, or an empty list when it named none.</summary>
    public static IReadOnlyList<string> Find(IDictionary<string, JsonElement>? unmatched)
    {
        if (unmatched is null || unmatched.Count == 0)
        {
            return [];
        }

        return Names
            .Where(name => unmatched.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static string Message(IReadOnlyList<string> found)
    {
        return $"{string.Join(", ", found)} {(found.Count == 1 ? "is" : "are")} no longer part of a property. "
            + "Prices, the transaction type, availability and promotional text belong to a marketplace listing.";
    }
}
