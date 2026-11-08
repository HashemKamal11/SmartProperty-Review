#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

public sealed record CreatePropertyRequest(
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
    Guid? CompoundId = null);
