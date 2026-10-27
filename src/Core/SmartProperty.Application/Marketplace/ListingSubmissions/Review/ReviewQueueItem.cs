using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review;

/// <summary>
/// One submission in the Platform Admin review queue, minimised to what a reviewer needs to pick it up.
/// </summary>
/// <remarks>
/// Deliberately carries no legal-party reference, owner or user identity, Workspace id, evidence digest or
/// identifier, internal assessment, or reviewer snapshot. Evidence is summarised as a count and one aggregate flag.
/// </remarks>
public sealed record ReviewQueueItem(
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
    ListingOwnerKind OwnerKind,
    DateTimeOffset SubmittedAt,
    int EvidenceCount,
    bool AllEvidenceClean);
