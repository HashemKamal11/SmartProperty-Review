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
/// <c>Daily</c>, <c>Weekly</c>, <c>Monthly</c> or <c>Yearly</c>; a Sale price never does, and the member is then left out of the JSON entirely.
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
            RequirePublishedAt(listing.PublishedAt),
            ToOptions(listing.PricingOptions),
            listing.PropertyId == Guid.Empty ? null : listing.PropertyId,
            CoverUrl(listing.PropertyId, listing.CoverImageId),
            listing.Bedrooms,
            listing.Bathrooms,
            listing.AreaSqm,
            listing.MatchPercentage,
            listing.IsFavorite);
    }

    /// <summary>The public photo file URL of the cover image, or null without a photo (or without a property).</summary>
    private static string? CoverUrl(Guid propertyId, Guid? coverImageId) =>
        propertyId != Guid.Empty && coverImageId is { } imageId && imageId != Guid.Empty
            ? SmartProperty.Api.Controllers.PropertyImagesController.UrlOf(propertyId, imageId)
            : null;

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
            RequirePublishedAt(listing.PublishedAt),
            ToOptions(listing.PricingOptions),
            ToTerms(listing.RentalTerms),
            ToBlocked(listing.BlockedRanges),
            listing.PropertyId == Guid.Empty ? null : listing.PropertyId,
            CoverUrl(listing.PropertyId, listing.CoverImageId),
            listing.Bedrooms,
            listing.Bathrooms,
            listing.AreaSqm,
            listing.MatchPercentage,
            listing.IsFavorite,
            PricePerSqm(listing),
            ToFacts(listing.PropertyId, listing.Property),
            listing.OtherListings?.Select(ToSummaryResponse).ToArray());
    }

    public static PublicPropertyCardResponse ToCardResponse(PublicPropertyCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.PropertyId == Guid.Empty || card.Listings.Count == 0)
        {
            throw Invalid("a property card without a property or a listing");
        }

        return new PublicPropertyCardResponse(
            card.PropertyId,
            ToPropertyType(card.PropertyType),
            ToLocation(card.Location),
            card.Listings.Select(ToSummaryResponse).ToArray(),
            CoverUrl(card.PropertyId, card.CoverImageId),
            card.Bedrooms,
            card.Bathrooms,
            card.AreaSqm,
            card.MatchPercentage,
            card.IsFavorite);
    }

    /// <summary>A sale price divided by the area, rounded to whole units; null for a rent or without an area.</summary>
    private static decimal? PricePerSqm(PublicListingDetail listing) =>
        listing.Purpose == ListingPurpose.Sale && listing.AreaSqm is > 0m and { } area
            ? Math.Round(listing.Price.Amount / area, 0, MidpointRounding.AwayFromZero)
            : null;

    private static PublicPropertyFactsResponse? ToFacts(Guid propertyId, PublicPropertyFacts? facts)
    {
        if (facts is null || propertyId == Guid.Empty)
        {
            return null;
        }

        return new PublicPropertyFactsResponse(
            facts.ImageIds.Select(imageId => SmartProperty.Api.Controllers.PropertyImagesController.UrlOf(propertyId, imageId)).ToArray(),
            facts.Amenities.Select(item => new PublicNamedItemResponse(item.Code, item.NameAr, item.NameEn)).ToArray(),
            facts.Views.Select(item => new PublicNamedItemResponse(item.Code, item.NameAr, item.NameEn)).ToArray(),
            facts.BuiltDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            facts.BuiltDate?.Year,
            facts.Finishing is { } finishing && Enum.IsDefined(finishing) ? finishing.ToString() : null,
            facts.Floor,
            facts.Furnished,
            facts.Place is { } place
                ? new PublicPlaceResponse(
                    ToLevel(place.Country),
                    ToLevel(place.City),
                    ToLevel(place.Area),
                    place.Compound is { } compound ? ToLevel(compound) : null)
                : null,
            facts.ApproximateLocation is { } point
                ? new PublicMapPointResponse(point.Latitude, point.Longitude)
                : null);
    }

    private static PublicPlaceLevelResponse ToLevel(PublicPlaceLevel level) =>
        new(level.Id, RequireText(level.NameEn, "place name"), RequireText(level.NameAr, "place name"));

    internal static IReadOnlyList<PublicMoneyResponse>? ToOptions(IReadOnlyList<PublicListingPrice>? options)
    {
        if (options is null)
        {
            return null;
        }

        return options
            .Select(option => new PublicMoneyResponse(
                option.Amount,
                RequireText(option.Currency, "currency"),
                ToRentPeriod(option.RentPeriod ?? throw Invalid("a pricing option without a period"))))
            .ToArray();
    }

    internal static PublicRentalTermsResponse? ToTerms(PublicRentalTerms? terms)
    {
        if (terms is null)
        {
            return null;
        }

        return new PublicRentalTermsResponse(
            RequireText(terms.TimeZoneId, "time zone"),
            terms.CheckInTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            terms.CheckOutTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            terms.MinimumStayDays,
            ToRanges(terms.Windows) ?? throw Invalid("rental terms without availability windows"));
    }

    private static IReadOnlyList<PublicDateRangeResponse>? ToBlocked(IReadOnlyList<PublicDateRange>? ranges) => ToRanges(ranges);

    private static IReadOnlyList<PublicDateRangeResponse>? ToRanges(IReadOnlyList<PublicDateRange>? ranges)
    {
        return ranges?
            .Select(range => new PublicDateRangeResponse(
                range.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                range.End.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
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
        // The registry's property type names are the contract values, so every defined type maps to its own name.
        return Enum.IsDefined(propertyType)
            ? propertyType.ToString()
            : throw Invalid("an unsupported property type");
    }

    private static string ToRentPeriod(RentPeriod rentPeriod)
    {
        return rentPeriod switch
        {
            RentPeriod.Monthly => "Monthly",
            RentPeriod.Yearly => "Yearly",
            RentPeriod.Daily => "Daily",
            RentPeriod.Weekly => "Weekly",
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
