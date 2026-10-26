#nullable enable
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// Maps the Application public listing read models to the public response DTOs, field by field.
/// </summary>
/// <remarks>
/// The mapper is the last line of defence before data leaves the API, so it fails closed. A read model that is
/// incomplete or inconsistent is a data-integrity fault, not something to display: it throws an
/// <see cref="InvalidOperationException"/> (answered as a generic 500 by the existing exception handler) instead of
/// skipping the row, substituting a default, or showing partial data. The exception message names the kind of fault
/// and never echoes any listing value.
///
/// Public strings are written out explicitly rather than taken from <c>Enum.ToString()</c>, so renaming a Domain
/// enum member cannot silently change the public contract. A Rent price always carries its rent period, which is
/// <c>Monthly</c> or <c>Yearly</c>; a Sale price never does, and the member is then left out of the JSON entirely.
/// Nothing is inferred: no rent period, currency, location or price is ever defaulted.
/// </remarks>
internal static class PublicListingMapper
{
    public static PublicListingSummaryResponse ToSummaryResponse(PublicListingSummary listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new PublicListingSummaryResponse(
            RequireListingId(listing.ListingId),
            ToPurpose(listing.Purpose),
            RequireText(listing.Title, "title"),
            ToPrice(listing.Price, listing.Purpose),
            ToPropertyType(listing.PropertyType),
            ToLocation(listing.Location),
            RequirePublishedAt(listing.PublishedAt));
    }

    public static PublicListingDetailResponse ToDetailResponse(PublicListingDetail listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new PublicListingDetailResponse(
            RequireListingId(listing.ListingId),
            ToPurpose(listing.Purpose),
            RequireText(listing.Title, "title"),
            RequireText(listing.Description, "description"),
            ToPrice(listing.Price, listing.Purpose),
            ToPropertyType(listing.PropertyType),
            ToLocation(listing.Location),
            RequirePublishedAt(listing.PublishedAt));
    }

    private static string ToPurpose(ListingPurpose purpose)
    {
        return purpose switch
        {
            ListingPurpose.Sale => "Sale",
            ListingPurpose.Rent => "Rent",
            _ => throw Invalid("an unsupported listing purpose")
        };
    }

    private static string ToPropertyType(PropertyType propertyType)
    {
        return propertyType switch
        {
            PropertyType.Land => "Land",
            PropertyType.Building => "Building",
            PropertyType.Unit => "Unit",
            _ => throw Invalid("an unsupported property type")
        };
    }

    private static string ToRentPeriod(RentPeriod rentPeriod)
    {
        return rentPeriod switch
        {
            RentPeriod.Monthly => "Monthly",
            RentPeriod.Yearly => "Yearly",
            _ => throw Invalid("an unsupported rent period")
        };
    }

    private static PublicMoneyResponse ToPrice(PublicListingPrice? price, ListingPurpose purpose)
    {
        if (price is null)
        {
            throw Invalid("a missing price");
        }

        var currency = RequireText(price.Currency, "currency");

        return purpose switch
        {
            ListingPurpose.Sale when price.RentPeriod is null =>
                new PublicMoneyResponse(price.Amount, currency),
            ListingPurpose.Sale =>
                throw Invalid("a rent period on a sale listing"),
            ListingPurpose.Rent when price.RentPeriod is { } rentPeriod =>
                new PublicMoneyResponse(price.Amount, currency, ToRentPeriod(rentPeriod)),
            ListingPurpose.Rent =>
                throw Invalid("a rent listing without a rent period"),
            _ => throw Invalid("an unsupported listing purpose")
        };
    }

    private static PublicLocationResponse ToLocation(PublicListingLocation? location)
    {
        if (location is null)
        {
            throw Invalid("a missing location");
        }

        return new PublicLocationResponse(
            RequireText(location.CountryCode, "country"),
            RequireText(location.City, "city"),
            RequireText(location.Region, "region"),
            RequireText(location.District, "district"));
    }

    private static Guid RequireListingId(Guid listingId)
    {
        return listingId == Guid.Empty ? throw Invalid("an empty listing id") : listingId;
    }

    private static DateTimeOffset RequirePublishedAt(DateTimeOffset publishedAt)
    {
        return publishedAt == default ? throw Invalid("a missing publication time") : publishedAt;
    }

    private static string RequireText(string? value, string field)
    {
        return string.IsNullOrWhiteSpace(value) ? throw Invalid($"a blank {field}") : value;
    }

    private static InvalidOperationException Invalid(string fault)
    {
        return new InvalidOperationException($"A public listing read model is invalid: {fault}.");
    }
}
