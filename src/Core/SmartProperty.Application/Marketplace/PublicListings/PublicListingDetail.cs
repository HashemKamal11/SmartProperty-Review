using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// One listing on its public detail page: every summary field plus the description. The privacy rules of
/// <see cref="PublicListingSummary"/> apply unchanged.
/// </summary>
public sealed record PublicListingDetail(
    Guid ListingId,
    ListingPurpose Purpose,
    string Title,
    string Description,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    DateTimeOffset PublishedAt,
    IReadOnlyList<PublicListingPrice>? PricingOptions = null,
    PublicRentalTerms? RentalTerms = null,
    IReadOnlyList<PublicDateRange>? BlockedRanges = null,
    Guid PropertyId = default,
    Guid? CoverImageId = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    decimal? AreaSqm = null,
    decimal? MatchPercentage = null,
    bool? IsFavorite = null,
    PublicPropertyFacts? Property = null,
    IReadOnlyList<PublicListingSummary>? OtherListings = null);
