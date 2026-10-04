using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

public sealed record CreatePropertyResult(
    Guid Id,
    PropertyType Type,
    PropertyStatus Status,
    CreatePropertyAddressResult Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreatePropertyAddressResult(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);
