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
    PropertyPhysicalDetails? Physical = null,
    PropertyPlaceDetails? Place = null,
    IReadOnlyList<PropertyImageDetails>? Images = null,
    decimal? MatchPercentage = null);

public sealed record PropertyAddressDetails(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>
/// The physical facts of a property that have no field of their own. Commercial terms (transaction type, prices,
/// availability, promotional text) belong to the Marketplace listing and are not part of the property.
/// </summary>
public sealed record PropertyPhysicalDetails(
    decimal? AreaSqm,
    int? Floor,
    bool? Furnished,
    string? TimeZoneId = null);

/// <summary>
/// Where a property is: its area and the city and country above it, and the compound when it is in one.
/// </summary>
public sealed record PropertyPlaceDetails(
    LocationDetails Country,
    LocationDetails City,
    LocationDetails Area,
    LocationDetails? Compound);


/// <summary>Read model of one view. <paramref name="Code"/> is what clients send when choosing views.</summary>
public sealed record ViewDetails(string Code, string NameAr, string NameEn);
