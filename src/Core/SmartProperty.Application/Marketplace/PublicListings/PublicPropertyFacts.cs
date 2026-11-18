using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The physical facts of a listed property for its public detail page, read live from the registry.
/// </summary>
/// <remarks>
/// Only what a visitor may see: the photos, the amenities and views, the build date, finishing, floor and furnishing,
/// the place names in both languages, and an approximate map point. The street address line, the postal code, the
/// exact coordinates and the owner are never copied here.
/// </remarks>
public sealed record PublicPropertyFacts(
    IReadOnlyList<Guid> ImageIds,
    IReadOnlyList<PublicNamedItem> Amenities,
    IReadOnlyList<PublicNamedItem> Views,
    DateOnly? BuiltDate,
    FinishingStatus? Finishing,
    int? Floor,
    bool? Furnished,
    PublicPlace? Place,
    PublicMapPoint? ApproximateLocation);

/// <summary>An amenity or a view: its code and its Arabic and English names.</summary>
public sealed record PublicNamedItem(string Code, string NameAr, string NameEn);

/// <summary>One level of the place tree, with the id to filter listings by it.</summary>
public sealed record PublicPlaceLevel(Guid Id, string NameEn, string NameAr);

/// <summary>Where the property is, from the country down to the compound when there is one.</summary>
public sealed record PublicPlace(
    PublicPlaceLevel Country,
    PublicPlaceLevel City,
    PublicPlaceLevel Area,
    PublicPlaceLevel? Compound);

/// <summary>
/// A map point rounded to <see cref="Decimals"/> decimal places (about 1 km), enough to show the neighbourhood and
/// never the building.
/// </summary>
public sealed record PublicMapPoint(decimal Latitude, decimal Longitude)
{
    public const int Decimals = 2;

    public static PublicMapPoint? Approximate(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon
            ? new PublicMapPoint(
                Math.Round(lat, Decimals, MidpointRounding.AwayFromZero),
                Math.Round(lon, Decimals, MidpointRounding.AwayFromZero))
            : null;
}
