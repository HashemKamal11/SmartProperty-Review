using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// One property on the public listings page, grouping its visible listings so a property advertised for both Sale and
/// Rent appears once.
/// </summary>
/// <remarks>
/// <see cref="Listings"/> holds only the property's listings that matched every filter of the search (Sale first, then
/// Rent), so a card never shows a price outside the range that was asked for. Without a listing filter (purpose, price,
/// rent period, dates) that is every visible listing of the property. The property facts are read live from the registry;
/// <see cref="MatchPercentage"/> and <see cref="IsFavorite"/> describe the signed-in viewer and are null for a visitor.
/// </remarks>
public sealed record PublicPropertyCard(
    Guid PropertyId,
    PropertyType PropertyType,
    PublicListingLocation Location,
    IReadOnlyList<PublicListingSummary> Listings,
    Guid? CoverImageId = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    decimal? AreaSqm = null,
    decimal? MatchPercentage = null,
    bool? IsFavorite = null);

/// <summary>One page of property cards; <see cref="TotalCount"/> counts properties, not listings.</summary>
public sealed record PublicPropertyCardPage(IReadOnlyList<PublicPropertyCard> Items, long TotalCount);
