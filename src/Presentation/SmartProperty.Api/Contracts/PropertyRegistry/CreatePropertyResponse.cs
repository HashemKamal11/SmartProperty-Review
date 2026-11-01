#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

public sealed record CreatePropertyResponse(
    Guid Id,
    string Type,
    string Status,
    PropertyAddressResponse Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    string? FinishingStatus = null,
    IReadOnlyList<AmenityResponse>? Amenities = null);

public sealed record PropertyAddressResponse(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);
