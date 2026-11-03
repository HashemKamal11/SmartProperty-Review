using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyMatching.Search;

/// <summary>
/// A free-text search, open to anyone. The AI search service ranks the properties; the platform keeps the match for every
/// result that is one of its own properties, but only when a signed-in user made the search.
/// </summary>
/// <param name="UserId">The signed-in user searching, or null for an anonymous search (nothing is stored then).</param>
/// <param name="Query">What they typed.</param>
/// <param name="TopK">How many results to ask for; <see cref="SearchPropertiesCommandHandler.DefaultTopK"/> when null.</param>
public sealed record SearchPropertiesCommand(Guid? UserId, string? Query, int? TopK = null)
    : ICommand<PropertySearchResult>;

public sealed record PropertySearchResult(
    string Query,
    string CorrectedQuery,
    int TotalAvailable,
    IReadOnlyList<PropertySearchItem> Items);

/// <summary>
/// One result. <paramref name="PropertyId"/> is set when the result is a property of this platform, and only
/// those have a stored match; any other listing the AI service knows is returned for display with a null id.
/// <paramref name="ExternalId"/> is the id this platform sent for the listing (the property id), null for a listing it did
/// not add; <paramref name="AiPropertyId"/> is the AI service's own reference, which is never one of our ids.
/// </summary>
/// <param name="MatchPercentage">The service's relevance score scaled to 0–100. A ranking signal, not a probability.</param>
public sealed record PropertySearchItem(
    Guid? PropertyId,
    string? ExternalId,
    string AiPropertyId,
    decimal MatchPercentage,
    string Description,
    AiSearchItem Details);
