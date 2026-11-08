#nullable enable
using SmartProperty.Api.Contracts.PropertyRegistry;

namespace SmartProperty.Api.Contracts.PropertyComparison;

/// <summary>
/// The comparison list with what the comparison screen shows. The table's rows (price, type, location, area, rooms,
/// year built, finishing, bathrooms, top features) are read from each item's <c>property</c>.
/// </summary>
/// <param name="TransactionType">Sale or Rent for the whole list, null while it is empty.</param>
/// <param name="CanCompare">True when the list holds at least <c>minItemsToCompare</c> properties.</param>
public sealed record PropertyComparisonResponse(
    IReadOnlyList<PropertyComparisonItemResponse> Items,
    int MaxItems,
    int MinItemsToCompare,
    bool CanCompare,
    string? TransactionType);

/// <param name="MatchPercentage">
/// The percentage of the user's latest AI match for this property; null when the AI gave none, so the screen can say so.
/// </param>
public sealed record PropertyComparisonItemResponse(
    int Position,
    DateTimeOffset AddedAt,
    decimal? MatchPercentage,
    string? MatchDescription,
    PropertyResponse Property);
