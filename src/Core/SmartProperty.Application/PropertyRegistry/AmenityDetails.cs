namespace SmartProperty.Application.PropertyRegistry;

/// <summary>Read model of one amenity. <paramref name="Code"/> is what clients send when choosing amenities.</summary>
public sealed record AmenityDetails(string Code, string NameAr, string NameEn);
