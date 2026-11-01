#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Admin;

/// <summary>
/// The outcome of a review, as the submission shows it.
/// </summary>
/// <remarks>
/// The reason exists only for a rejection and is omitted otherwise. Nothing identifies the reviewer.
/// </remarks>
public sealed record ReviewDecisionResponse(
    bool Approved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    DateTimeOffset DecidedAt);
