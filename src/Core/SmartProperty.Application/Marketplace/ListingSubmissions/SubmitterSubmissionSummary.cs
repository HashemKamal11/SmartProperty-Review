using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.ListingSubmissions;

/// <summary>
/// One of the submitter's own listing submissions, as shown in their submission list.
/// </summary>
/// <remarks>
/// Deliberately carries no property id, legal-party reference, owner identity, Workspace id, evidence, digest,
/// verification fact, assessment or reviewer identity, and no user id of any kind. <see cref="ListingId"/> and
/// <see cref="SubmissionId"/> are the only identifiers. The price and location reuse the public value records,
/// which hold plain values only.
/// </remarks>
public sealed record SubmitterSubmissionSummary(
    Guid SubmissionId,
    Guid ListingId,
    ListingPurpose Purpose,
    int Version,
    ListingSubmissionStatus Status,
    string Title,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    ListingRepresentationKind Representation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentlyPublished);
