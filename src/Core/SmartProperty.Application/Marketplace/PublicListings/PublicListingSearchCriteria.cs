using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The validated, normalised filters of a public listing search, as handed to the read repository.
/// </summary>
/// <remarks>
/// Produced only by the search validator, so a repository can rely on these invariants:
/// <list type="bullet">
/// <item>Country and currency are upper-case; location values are trimmed; blank filters are absent.</item>
/// <item>A price bound or a price sort comes with a purpose and a currency, and a Rent price comparison also comes
/// with a <see cref="RentPeriod"/>, so prices of different purposes, currencies or rent periods are never compared.</item>
/// <item><see cref="RentPeriod"/> is present only when <see cref="Purpose"/> is Rent.</item>
/// <item>Minimum and maximum prices are not negative and the minimum does not exceed the maximum.</item>
/// </list>
/// </remarks>
public sealed record PublicListingSearchCriteria(
    ListingPurpose? Purpose,
    PropertyType? PropertyType,
    string? CountryCode,
    string? Region,
    string? City,
    string? District,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Currency,
    RentPeriod? RentPeriod,
    PublicListingSortOrder Sort);
