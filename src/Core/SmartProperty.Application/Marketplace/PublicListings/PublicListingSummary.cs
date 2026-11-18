using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// One listing as shown in public search results, built only from the explicitly published, approved submission
/// snapshot.
/// </summary>
/// <remarks>
/// The identifiers are the listing id, the property id (favourites, sharing and photos are keyed by it, and the
/// public photo URL already carries it) and the cover photo id. The submission id and version, every status, and all
/// owner, evidence, review and Workspace data are intentionally absent.
/// <see cref="MatchPercentage"/> and <see cref="IsFavorite"/> describe the signed-in viewer and are null for a visitor.
/// </remarks>
public sealed record PublicListingSummary(
    Guid ListingId,
    ListingPurpose Purpose,
    string Title,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    DateTimeOffset PublishedAt,
    IReadOnlyList<PublicListingPrice>? PricingOptions = null,
    Guid PropertyId = default,
    Guid? CoverImageId = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    decimal? AreaSqm = null,
    decimal? MatchPercentage = null,
    bool? IsFavorite = null);
