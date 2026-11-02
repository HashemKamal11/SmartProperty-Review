using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// Read model of one canonical property, shared by the get-by-id and list use cases.
/// </summary>
/// <remarks>
/// Projected directly by Persistence, so reading a property never materializes the <see cref="Property"/>
/// aggregate. Archived properties are returned like any other: the registry never hides a record it holds.
/// </remarks>
public sealed record PropertyDetails(
    Guid Id,
    PropertyType Type,
    PropertyStatus Status,
    PropertyAddressDetails Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    FinishingStatus? FinishingStatus = null,
    IReadOnlyList<AmenityDetails>? Amenities = null,
    IReadOnlyList<ViewDetails>? Views = null,
    PropertyListingDetails? Listing = null,
    AiSyncDetails? AiSync = null,
    PropertyPlaceDetails? Place = null,
    IReadOnlyList<PropertyImageDetails>? Images = null);

public sealed record PropertyAddressDetails(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>The market-facing facts of a property. Currency is not held; it follows the area's country.</summary>
public sealed record PropertyListingDetails(
    TransactionType? TransactionType,
    decimal? Price,
    decimal? AreaSqm,
    int? Floor,
    bool? Furnished,
    ListingStatus ListingStatus,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn);

/// <summary>
/// Where a property is: its area and the city and country above it, and the compound when it is in one.
/// </summary>
public sealed record PropertyPlaceDetails(
    LocationDetails Country,
    LocationDetails City,
    LocationDetails Area,
    LocationDetails? Compound);

/// <summary>Whether the AI search service holds the property, and when or why not.</summary>
public sealed record AiSyncDetails(AiSyncStatus Status, DateTimeOffset? SyncedAt, string? Error);

/// <summary>Read model of one view. <paramref name="Code"/> is what clients send when choosing views.</summary>
public sealed record ViewDetails(string Code, string NameAr, string NameEn);
