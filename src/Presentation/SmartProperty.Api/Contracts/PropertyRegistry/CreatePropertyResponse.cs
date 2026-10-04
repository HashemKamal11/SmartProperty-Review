#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

public sealed record CreatePropertyResponse(
    Guid Id,
    string Type,
    string Status,
    PropertyAddressResponse Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PropertyAddressResponse(
    string CountryCode,
    string? City,
    string? Region,
    string? District,
    string? AddressLine,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude);
