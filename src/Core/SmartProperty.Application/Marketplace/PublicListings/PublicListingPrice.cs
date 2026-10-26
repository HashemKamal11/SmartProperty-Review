using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The price of a public listing. <see cref="RentPeriod"/> is present exactly when the listing is a Rent listing.
/// </summary>
public sealed record PublicListingPrice(decimal Amount, string Currency, RentPeriod? RentPeriod);
