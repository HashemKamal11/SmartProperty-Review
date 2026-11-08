using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyComparison;

/// <summary>
/// The user's comparison list with everything the comparison screen shows: each property in full, in the order it was
/// added, and the smart-pick match for it when the AI search gave one.
/// </summary>
/// <param name="TransactionType">Sale or Rent for the whole list; null while it is empty. All items share it.</param>
/// <param name="CanCompare">True when the list holds enough properties to show the comparison table.</param>
public sealed record PropertyComparisonDetails(
    IReadOnlyList<PropertyComparisonItemDetails> Items,
    int MaxItems,
    int MinItemsToCompare,
    bool CanCompare,
    TransactionType? TransactionType)
{
    public static PropertyComparisonDetails Of(
        IReadOnlyList<PropertyComparisonItemDetails> items,
        int maxItems,
        int minItemsToCompare)
    {
        return new PropertyComparisonDetails(
            items,
            maxItems,
            minItemsToCompare,
            items.Count >= minItemsToCompare,
            items.Count == 0 ? null : items[0].Property.Listing?.TransactionType);
    }
}

/// <param name="MatchPercentage">
/// The percentage of this user's latest AI match for the property, or null when the AI never gave one.
/// </param>
public sealed record PropertyComparisonItemDetails(
    int Position,
    DateTimeOffset AddedAt,
    PropertyDetails Property,
    decimal? MatchPercentage,
    string? MatchDescription);
