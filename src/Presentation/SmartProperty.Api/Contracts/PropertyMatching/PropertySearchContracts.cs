#nullable enable
using SmartProperty.Api.Contracts.PropertyRegistry;
namespace SmartProperty.Api.Contracts.PropertyMatching;

/// <summary>A user's free-text search. <c>topK</c> is how many results to return (default 3, at most 10).</summary>
public sealed record PropertySearchRequest(string? Query, int? TopK = null);

public sealed record PropertySearchResponse(
    string Query,
    string CorrectedQuery,
    int TotalAvailable,
    IReadOnlyList<PropertySearchItemResponse> Items);

/// <summary>
/// One result. <c>propertyId</c> is set when it is a property of this platform; any other listing the AI service
/// knows has a null <c>propertyId</c> and is shown from <c>details</c> only.
/// <c>externalId</c> is the id this platform sent for the advertisement (the LISTING id, or null for a record it did not add);
/// <c>listing</c> is that advertisement as the marketplace shows it now, set only when it is still visible;
/// <c>aiPropertyId</c> is the AI service's own reference.
/// </summary>
/// <param name="MatchPercentage">The AI relevance score scaled to 0–100. A ranking signal, not a probability.</param>
public sealed record PropertySearchItemResponse(
    Guid? PropertyId,
    string? ExternalId,
    string AiPropertyId,
    decimal MatchPercentage,
    string Description,
    PropertySearchDetailsResponse Details,
    PropertyResponse? Property = null,
    SmartProperty.Api.Contracts.Marketplace.Public.PublicListingSummaryResponse? Listing = null);

public sealed record PropertySearchDetailsResponse(
    string? City,
    string? CityAr,
    string? Area,
    string? AreaAr,
    string? District,
    string? DistrictAr,
    string? Compound,
    string? CompoundAr,
    string? PropertyType,
    string? PropertyTypeAr,
    string? TransactionType,
    string? TransactionTypeAr,
    decimal? Price,
    string? Currency,
    decimal? AreaSqm,
    int? Bedrooms,
    int? Bathrooms,
    string? FinishingStatus,
    bool? Furnished,
    string? View,
    string? Amenities,
    string? ListingStatus,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn);
