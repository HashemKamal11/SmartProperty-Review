#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>One amenity. Send <c>code</c> in <c>amenityCodes</c> when creating or updating a property.</summary>
public sealed record AmenityResponse(string Code, string NameAr, string NameEn);
