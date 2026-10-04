#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

public sealed record CreatePropertyRequest(
    string? Type,
    string? CountryCode,
    string? City = null,
    string? Region = null,
    string? District = null,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null);
