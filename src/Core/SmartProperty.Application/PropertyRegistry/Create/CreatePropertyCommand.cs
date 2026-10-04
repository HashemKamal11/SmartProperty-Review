using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Create;

public sealed record CreatePropertyCommand(
    string? Type,
    string? CountryCode,
    string? City = null,
    string? Region = null,
    string? District = null,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null) : ICommand<CreatePropertyResult>;
