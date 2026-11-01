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
    IReadOnlyList<AmenityDetails>? Amenities = null);

public sealed record PropertyAddressDetails(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);
