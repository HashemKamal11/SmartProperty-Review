namespace SmartProperty.Application.Abstractions.Ai;

/// <summary>One listing in the AI service vocabulary: the strings are its closed-list values.</summary>
public sealed record AiListing(
    int AreaId,
    int? CompoundId,
    string PropertyType,
    string TransactionType,
    decimal Price,
    decimal AreaSqm,
    int Bedrooms,
    int Bathrooms,
    int? Floor,
    string? FinishingStatus,
    bool? Furnished,
    string? View,
    IReadOnlyList<string> Amenities,
    string ListingStatus,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn);

/// <summary>The service's answer to adding a listing. <paramref name="Created"/> is false when it replaced one.</summary>
public sealed record AiListingAcknowledgement(bool Created);

public sealed record AiPlace(int Id, string NameEn, string NameAr);

/// <summary>A search answer: the ranked items and how many listings matched in all.</summary>
public sealed record AiSearchOutcome(
    string Query,
    string CorrectedQuery,
    int TotalAvailable,
    IReadOnlyList<AiSearchItem> Items);

/// <summary>
/// One ranked search result. <paramref name="PropertyId"/> is the AI service's own reference for the listing.
/// <paramref name="ExternalId"/> is the id this platform sent when it added the listing, which for a property of this
/// platform is the property's id, and null for any listing this platform did not add.
/// </summary>
public sealed record AiSearchItem(
    string PropertyId,
    string? ExternalId,
    double? Score,
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
