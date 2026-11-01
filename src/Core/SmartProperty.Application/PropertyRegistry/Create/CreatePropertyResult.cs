using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

public sealed record CreatePropertyResult(
    Guid Id,
    PropertyType Type,
    PropertyStatus Status,
    CreatePropertyAddressResult Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    FinishingStatus? FinishingStatus = null,
    IReadOnlyList<AmenityDetails>? Amenities = null);

public sealed record CreatePropertyAddressResult(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);
