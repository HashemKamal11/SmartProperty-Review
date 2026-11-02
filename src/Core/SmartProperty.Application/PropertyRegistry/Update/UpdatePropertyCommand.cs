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
    decimal? Longitude = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    string? FinishingStatus = null,
    IReadOnlyList<string>? AmenityCodes = null,
    IReadOnlyList<string>? ViewCodes = null,
    string? TransactionType = null,
    decimal? Price = null,
    decimal? AreaSqm = null,
    int? Floor = null,
    bool? Furnished = null,
    string? ListingStatus = null,
    string? TitleAr = null,
    string? TitleEn = null,
    string? DescriptionAr = null,
    string? DescriptionEn = null,
    Guid? AreaId = null,
    Guid? CompoundId = null) : ICommand<PropertyDetails>;
