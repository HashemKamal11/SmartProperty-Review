#nullable enable
using SmartProperty.Api.Contracts.Marketplace.Public;

namespace SmartProperty.Api.Contracts.Marketplace.Admin;

/// <summary>
/// One submission in the Platform Admin review queue.
/// </summary>
/// <remarks>
/// A closed vocabulary. Every member comes from the Application review read model; there is deliberately no legal
/// party reference, user id, Workspace id, evidence identifier or digest, assessment, authority or reviewer
/// snapshot, and no way to learn who the reviewer is. Evidence is summarised as a count and one aggregate flag.
/// Enumerations are their names, as the public contract does.
/// </remarks>
public sealed record ReviewQueueItemResponse(
    Guid SubmissionId,
    Guid ListingId,
    string Purpose,
    int Version,
    string Status,
    string Title,
    PublicMoneyResponse Price,
    string PropertyType,
    PublicLocationResponse Location,
    string Representation,
    string OwnerKind,
    DateTimeOffset SubmittedAt,
    int EvidenceCount,
    bool AllEvidenceClean);
