using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// Validates and normalises the search filters of a public listing query.
/// </summary>
/// <remarks>
/// This is request validation only. Whether a listing is complete enough to be published, and whether it is
/// visible, are decided elsewhere: completeness at submission and publication, visibility by the read query. A
/// filter value that matches nothing simply returns an empty page; nothing here invents a default location, price,
/// currency or rent period.
///
/// Price rules (R1): a price filter or a price sort needs a purpose and a currency, and a Rent price comparison
/// also needs a rent period, so a Sale price is never compared with a Rent price, amounts in different currencies
/// are never compared, and a Monthly rent is never compared with a Yearly one. A rent period is valid only with the
/// Rent purpose. A search without price filtering or sorting may span both purposes.
/// </remarks>
internal static class PublicListingSearchFilterValidator
{
    public static Result<PublicListingSearchCriteria> Validate(
        ListingPurpose? purpose,
        PropertyType? propertyType,
        string? countryCode,
        string? region,
        string? city,
        string? district,
        decimal? minPrice,
        decimal? maxPrice,
        string? currency,
        RentPeriod? rentPeriod,
        PublicListingSortOrder? sort)
    {
        if (purpose is { } listingPurpose && !Enum.IsDefined(listingPurpose))
        {
            return Fail("The listing purpose is not supported.");
        }

        if (propertyType is { } type && !Enum.IsDefined(type))
        {
            return Fail("The property type is not supported.");
        }

        if (rentPeriod is { } period && !Enum.IsDefined(period))
        {
            return Fail("The rent period is not supported.");
        }

        var normalizedCountry = Normalize(countryCode);

        if (normalizedCountry is not null && !IsAsciiLetters(normalizedCountry, 2))
        {
            return Fail("Country code must contain exactly two ASCII letters.");
        }

        var normalizedCurrency = Normalize(currency);

        if (normalizedCurrency is not null && !IsAsciiLetters(normalizedCurrency, 3))
        {
            return Fail("Currency must contain exactly three ASCII letters.");
        }

        if (minPrice < 0m || maxPrice < 0m)
        {
            return Fail("Prices must not be negative.");
        }

        if (minPrice is { } minimum && maxPrice is { } maximum && minimum > maximum)
        {
            return Fail("The minimum price must not exceed the maximum price.");
        }

        var sortOrder = sort ?? PublicListingSortOrder.Newest;

        if (!Enum.IsDefined(sortOrder))
        {
            return Fail("The sort order is not supported.");
        }

        var comparesPrices = minPrice is not null
            || maxPrice is not null
            || sortOrder is PublicListingSortOrder.PriceAsc or PublicListingSortOrder.PriceDesc;

        if (comparesPrices && purpose is null)
        {
            return Fail("A listing purpose is required when filtering or sorting by price.");
        }

        if (comparesPrices && normalizedCurrency is null)
        {
            return Fail("A currency is required when filtering or sorting by price.");
        }

        if (rentPeriod is not null && purpose != ListingPurpose.Rent)
        {
            return Fail("A rent period is valid only for Rent listings.");
        }

        if (comparesPrices && purpose == ListingPurpose.Rent && rentPeriod is null)
        {
            return Fail("A rent period is required when filtering or sorting Rent listings by price.");
        }

        return Result<PublicListingSearchCriteria>.Success(new PublicListingSearchCriteria(
            purpose,
            propertyType,
            normalizedCountry?.ToUpperInvariant(),
            Normalize(region),
            Normalize(city),
            Normalize(district),
            minPrice,
            maxPrice,
            normalizedCurrency?.ToUpperInvariant(),
            rentPeriod,
            sortOrder));
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool IsAsciiLetters(string value, int length)
    {
        return value.Length == length && value.All(char.IsAsciiLetter);
    }

    private static Result<PublicListingSearchCriteria> Fail(string description)
    {
        return Result<PublicListingSearchCriteria>.Failure(PublicListingErrors.ValidationFailed(description));
    }
}
