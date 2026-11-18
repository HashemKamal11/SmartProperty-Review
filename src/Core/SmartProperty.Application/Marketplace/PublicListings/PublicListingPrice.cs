using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The price of a public listing. <see cref="RentPeriod"/> is present exactly when the listing is a Rent listing.
/// </summary>
/// <remarks>
/// <see cref="Negotiable"/> is a sale price's only, and null when the owner did not state it. <see cref="Deposit"/> is
/// the security deposit asked with a rent price, in the same currency, and null when none was stated.
/// </remarks>
public sealed record PublicListingPrice(
    decimal Amount,
    string Currency,
    RentPeriod? RentPeriod,
    bool? Negotiable = null,
    decimal? Deposit = null);
