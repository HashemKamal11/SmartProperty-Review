using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// One listing as shown in public search results, built only from the explicitly published, approved submission
/// snapshot.
/// </summary>
/// <remarks>
/// <see cref="ListingId"/> is the only identifier. The property id, the submission id and version, every status, and
/// all owner, evidence, review and Workspace data are intentionally absent.
/// </remarks>
public sealed record PublicListingSummary(
    Guid ListingId,
    ListingPurpose Purpose,
    string Title,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    DateTimeOffset PublishedAt);
