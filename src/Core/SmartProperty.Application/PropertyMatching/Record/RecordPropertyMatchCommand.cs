using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyMatching.Record;

/// <summary>
/// Stores one AI match result: how well <paramref name="PropertyId"/> fits what <paramref name="UserId"/> searched
/// for, with the AI description.
/// </summary>
public sealed record RecordPropertyMatchCommand(
    Guid? UserId,
    Guid? PropertyId,
    decimal? MatchPercentage,
    string? Description,
    string? Query) : ICommand<PropertyMatchDetails>;

public sealed record PropertyMatchDetails(
    Guid Id,
    Guid UserId,
    Guid PropertyId,
    decimal MatchPercentage,
    string Description,
    string Query,
    DateTimeOffset CreatedAt);
