using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review;

/// <summary>
/// One submission as a Platform Admin reviewer sees it: every queue field plus the description, the property it
/// concerns, the validity window of the declared authority, its place in the version chain, and the review decision,
/// if there is one.
/// </summary>
/// <remarks>
/// The initial review read deliberately omits the legal-party reference, the linked and acting user identities, the
/// Workspace id, evidence identifiers and digests, the scan assessments, and the reviewer snapshot with its
/// independence facts. Those are confidential, and exposing any of them needs a separate approval. Evidence is
/// summarised as a count and one aggregate flag. The submission itself is immutable; this is a read of its history.
/// </remarks>
public sealed record ReviewSubmissionDetail(
    Guid SubmissionId,
    Guid ListingId,
    Guid PropertyId,
    Guid? PredecessorSubmissionId,
    ListingPurpose Purpose,
    int Version,
    ListingSubmissionStatus Status,
    string Title,
    string Description,
    PublicListingPrice Price,
    PropertyType PropertyType,
    PublicListingLocation Location,
    ListingRepresentationKind Representation,
    ListingOwnerKind OwnerKind,
    DateTimeOffset AuthorityValidFrom,
    DateTimeOffset? AuthorityValidUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset SubmittedAt,
    DateTimeOffset UpdatedAt,
    int EvidenceCount,
    bool AllEvidenceClean,
    bool IsCurrentlyPublished,
    ReviewDecisionSummary? Decision);

/// <summary>
/// The recorded outcome of a review: whether it was approved, when, and for a rejection the reason. The reviewer's
/// identity and independence facts are never part of it.
/// </summary>
public sealed record ReviewDecisionSummary(
    bool Approved,
    string? Reason,
    DateTimeOffset DecidedAt);
