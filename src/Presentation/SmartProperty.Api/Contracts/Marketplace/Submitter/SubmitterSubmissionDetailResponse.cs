#nullable enable
using System.Text.Json.Serialization;
using SmartProperty.Api.Contracts.Marketplace.Public;

namespace SmartProperty.Api.Contracts.Marketplace.Submitter;

/// <summary>
/// One of the authenticated user's own submissions, in full.
/// </summary>
/// <remarks>
/// The summary plus the description and the review outcome. <c>submittedAt</c> is omitted for a Draft and
/// <c>decision</c> until the submission has been decided. The same closed vocabulary as the summary applies: nothing
/// that identifies a party, a user, a Workspace, a piece of evidence or a reviewer is ever part of this contract.
/// </remarks>
public sealed record SubmitterSubmissionDetailResponse(
    Guid SubmissionId,
    Guid ListingId,
    string Purpose,
    int Version,
    string Status,
    string Title,
    string Description,
    PublicMoneyResponse Price,
    string PropertyType,
    PublicLocationResponse Location,
    string Representation,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    bool IsCurrentlyPublished,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SubmitterSubmissionDecisionResponse? Decision);
