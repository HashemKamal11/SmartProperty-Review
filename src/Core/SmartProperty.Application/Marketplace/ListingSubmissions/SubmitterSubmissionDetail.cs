using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.ListingSubmissions;

/// <summary>
/// One of the submitter's own listing submissions in full: every summary field plus the description and the review
/// decision, if there is one.
/// </summary>
/// <remarks>
/// The privacy rules of <see cref="SubmitterSubmissionSummary"/> apply unchanged.
/// </remarks>
public sealed record SubmitterSubmissionDetail(
    Guid SubmissionId,
    Guid ListingId,
    ListingPurpose Purpose,
    int Version,
    ListingSubmissionStatus Status,
    string Title,
    string Description,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    ListingRepresentationKind Representation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentlyPublished,
    SubmitterSubmissionDecision? Decision);

/// <summary>
/// The outcome of an independent review, as the submitter may see it: whether it was approved, when it was decided,
/// and, for a rejection only, the reason. The reviewer's identity and independence facts are never part of it.
/// </summary>
public sealed record SubmitterSubmissionDecision(
    bool Approved,
    string? Reason,
    DateTimeOffset DecidedAt);
