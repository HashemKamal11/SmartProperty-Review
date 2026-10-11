using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Update;

/// <summary>
/// Full replacement of every mutable detail of one property. An optional value left null clears it.
/// </summary>
public sealed record UpdatePropertyCommand(
    Guid Id,
    string? Type,
    string? CountryCode,
    string? City = null,
    string? Region = null,
    string? District = null,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null) : ICommand<PropertyDetails>;
