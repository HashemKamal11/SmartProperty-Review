#nullable enable
using SmartProperty.Api.Contracts.PropertyImages;
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// One canonical property, as returned by the read endpoints, by an update, and by an archive. The JSON shape
/// intentionally matches <see cref="CreatePropertyResponse"/>; the types are kept separate so each contract can
/// evolve on its own.
/// </summary>
public sealed record PropertyResponse(
    Guid Id,
    string Type,
    string Status,
    PropertyAddressResponse Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    string? FinishingStatus = null,
    IReadOnlyList<AmenityResponse>? Amenities = null,
    IReadOnlyList<ViewResponse>? Views = null,
    PropertyPhysicalResponse? Physical = null,
    PropertyPlaceResponse? Place = null,
    IReadOnlyList<PropertyImageResponse>? Images = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    decimal? MatchPercentage = null);
