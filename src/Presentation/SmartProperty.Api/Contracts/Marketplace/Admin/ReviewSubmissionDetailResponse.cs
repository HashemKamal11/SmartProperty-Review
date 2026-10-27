#nullable enable
using System.Text.Json.Serialization;
using SmartProperty.Api.Contracts.Marketplace.Public;

namespace SmartProperty.Api.Contracts.Marketplace.Admin;

/// <summary>
/// One submission as a Platform Admin reviewer sees it.
/// </summary>
/// <remarks>
/// A closed vocabulary, like <see cref="ReviewQueueItemResponse"/> plus the review context. <c>PropertyId</c> is the
/// canonical registry property the submission is about, present in the Application detail model: a reviewer needs it
/// to check the submission against that property. It identifies a platform-owned record, not a person or party.
/// The legal-party reference, linked and acting user ids, Workspace id, evidence identifiers and digests, assessments
/// and the authority and reviewer snapshots are never part of this contract.
/// </remarks>
public sealed record ReviewSubmissionDetailResponse(
    Guid SubmissionId,
    Guid ListingId,
    Guid PropertyId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PredecessorSubmissionId,
    string Purpose,
    int Version,
    string Status,
    string Title,
    string Description,
    PublicMoneyResponse Price,
    string PropertyType,
    PublicLocationResponse Location,
    string Representation,
    string OwnerKind,
    DateTimeOffset AuthorityValidFrom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AuthorityValidUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset SubmittedAt,
    DateTimeOffset UpdatedAt,
    int EvidenceCount,
    bool AllEvidenceClean,
    bool IsCurrentlyPublished,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewDecisionResponse? Decision);
