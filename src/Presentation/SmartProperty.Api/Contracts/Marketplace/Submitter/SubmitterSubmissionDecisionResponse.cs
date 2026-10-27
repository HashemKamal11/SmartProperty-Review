#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Submitter;

/// <summary>
/// The outcome of the review of a submission, as its submitter sees it.
/// </summary>
/// <remarks>
/// Independent of the Platform Admin decision contract on purpose: the two audiences can diverge without one
/// contract dragging the other along. The reason exists only for a rejection and is omitted otherwise. Nothing
/// identifies the reviewer.
/// </remarks>
public sealed record SubmitterSubmissionDecisionResponse(
    bool Approved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    DateTimeOffset DecidedAt);
