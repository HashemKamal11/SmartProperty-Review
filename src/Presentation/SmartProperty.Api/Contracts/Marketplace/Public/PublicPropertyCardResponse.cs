#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// One property on the public listings page. <see cref="Listings"/> holds its listings that matched the filters, Sale
/// first: one entry for a Sale-only or Rent-only result, two when the property is offered both ways and both match.
/// Each listing keeps its own id (open its detail page with it), title and price.
/// </summary>
public sealed record PublicPropertyCardResponse(
    Guid PropertyId,
    string PropertyType,
    PublicLocationResponse Location,
    IReadOnlyList<PublicListingSummaryResponse> Listings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CoverImageUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Bedrooms = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Bathrooms = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? AreaSqm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MatchPercentage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsFavorite = null);
