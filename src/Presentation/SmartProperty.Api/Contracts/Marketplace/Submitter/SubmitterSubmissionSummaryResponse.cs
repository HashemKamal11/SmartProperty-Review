#nullable enable
using System.Text.Json.Serialization;
using SmartProperty.Api.Contracts.Marketplace.Public;

namespace SmartProperty.Api.Contracts.Marketplace.Submitter;

/// <summary>
/// One of the authenticated user's own submissions, in a list.
/// </summary>
/// <remarks>
/// A closed vocabulary. Every member comes from the Application submitter read model. There is deliberately no
/// legal-party reference, acting or linked user id, Workspace id, evidence, digest, assessment, snapshot or reviewer
/// identity, and no way to name whose submissions to read: that is the authenticated principal, never a request
/// value. A Draft has no submission time, so <c>submittedAt</c> is omitted for one. Enumerations are their names, as
/// the public contract does.
/// </remarks>
public sealed record SubmitterSubmissionSummaryResponse(
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
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentlyPublished);
