#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>One view. Send <c>code</c> in <c>viewCodes</c> when creating or updating a property.</summary>
public sealed record ViewResponse(string Code, string NameAr, string NameEn);

/// <summary>
/// The physical facts of a property that have no field of their own. Where it is lives in <c>place</c>. How it is
/// advertised (sale, rent, prices, promotional text) is not part of a property: see the marketplace listings.
/// </summary>
public sealed record PropertyPhysicalResponse(
    decimal? AreaSqm,
    int? Floor,
    bool? Furnished,
    string? TimeZoneId = null);

/// <summary>
/// Where a property is. Choose it with <c>areaId</c> (and <c>compoundId</c>) from <c>GET /api/locations</c>; the
/// city and country above the area are filled in.
/// </summary>
public sealed record PropertyPlaceResponse(
    PlaceResponseItem Country,
    PlaceResponseItem City,
    PlaceResponseItem Area,
    PlaceResponseItem? Compound);

public sealed record PlaceResponseItem(Guid Id, string NameEn, string NameAr);
