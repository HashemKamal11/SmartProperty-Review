#nullable enable
namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// The coarse, public location of a listed property: country, city, and optionally region and district.
/// </summary>
/// <remarks>
/// Deliberately nothing finer. The street address, postal code and coordinates that the property registry holds
/// are private and have no member here, so they cannot be serialized by accident. The values come from the
/// canonical property's address.
/// </remarks>
public sealed record PublicLocationResponse(
    string CountryCode,
    string City,
    string? Region,
    string? District);
