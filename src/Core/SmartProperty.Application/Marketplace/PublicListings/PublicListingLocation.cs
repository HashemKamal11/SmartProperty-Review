namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The coarse public location of a listing, taken from the published submission snapshot. Every part is required.
/// There is deliberately no street address, postal code or coordinate.
/// </summary>
public sealed record PublicListingLocation(
    string CountryCode,
    string Region,
    string City,
    string District);
