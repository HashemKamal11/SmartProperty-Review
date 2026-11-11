using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Update;

/// <summary>
/// Full replacement of every mutable detail of one property. An optional value left null clears it.
/// </summary>
public sealed record UpdatePropertyCommand(
    Guid Id,
    string? Type,
    Guid? AreaId,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    string? FinishingStatus = null,
    IReadOnlyList<string>? AmenityCodes = null,
    IReadOnlyList<string>? ViewCodes = null,
    decimal? AreaSqm = null,
    int? Floor = null,
    bool? Furnished = null,
    string? TimeZoneId = null,
    Guid? CompoundId = null) : ICommand<PropertyDetails>;
