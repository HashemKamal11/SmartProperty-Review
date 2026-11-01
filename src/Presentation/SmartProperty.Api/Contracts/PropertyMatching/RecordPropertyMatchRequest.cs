#nullable enable
namespace SmartProperty.Api.Contracts.PropertyMatching;

/// <summary>
/// One match result sent by the AI system. The user id is the one the front end passed to the AI system.
/// </summary>
public sealed record RecordPropertyMatchRequest(
    Guid? UserId,
    Guid? PropertyId,
    decimal? MatchPercentage,
    string? Description,
    string? Query);

public sealed record PropertyMatchResponse(
    Guid Id,
    Guid UserId,
    Guid PropertyId,
    decimal MatchPercentage,
    string Description,
    string Query,
    DateTimeOffset CreatedAt);
