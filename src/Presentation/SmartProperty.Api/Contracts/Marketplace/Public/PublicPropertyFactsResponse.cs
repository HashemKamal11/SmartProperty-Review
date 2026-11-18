#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// The listed property on its public detail page: every photo URL (the first is the cover), amenities and views with
/// Arabic and English names, build date, finishing, floor, furnishing, the place in both languages, and an approximate
/// map point (rounded to about 1 km). Unknown values are left out.
/// </summary>
public sealed record PublicPropertyFactsResponse(
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<PublicNamedItemResponse> Amenities,
    IReadOnlyList<PublicNamedItemResponse> Views,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuiltDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BuiltYear = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Finishing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Floor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Furnished = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublicPlaceResponse? Place = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublicMapPointResponse? ApproximateLocation = null);

/// <summary>An amenity or a view: the code to send in filters, and its names to show.</summary>
public sealed record PublicNamedItemResponse(string Code, string NameAr, string NameEn);

/// <summary>One level of the place tree; the id filters listings (<c>cityId</c>, <c>areaId</c>, <c>compoundId</c>).</summary>
public sealed record PublicPlaceLevelResponse(Guid Id, string NameEn, string NameAr);

/// <summary>The property's place from the country down to the compound.</summary>
public sealed record PublicPlaceResponse(
    PublicPlaceLevelResponse Country,
    PublicPlaceLevelResponse City,
    PublicPlaceLevelResponse Area,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublicPlaceLevelResponse? Compound = null);

/// <summary>An approximate map point, rounded to 2 decimal places (about 1 km).</summary>
public sealed record PublicMapPointResponse(decimal Latitude, decimal Longitude);
