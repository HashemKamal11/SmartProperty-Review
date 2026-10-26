using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// Validates and normalises the search filters of a public listing query.
/// </summary>
/// <remarks>
/// This is request validation only. Whether a listing is complete enough to be published, and whether it is
/// visible, are decided elsewhere: completeness at submission and publication, visibility by the read query. A
/// filter value that matches nothing simply returns an empty page; nothing here invents a default location, price
/// or currency.
/// </remarks>
internal static class PublicListingSearchFilterValidator
{
    public static Result<PublicListingSearchFilters> Validate(
        string? countryCode,
        string? city,
        string? region,
        string? district,
        decimal? minPrice,
        decimal? maxPrice,
        string? currency,
        PublicListingSortOrder? sort)
    {
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

        if (comparesPrices && normalizedCurrency is null)
        {
            return Fail("A currency is required when filtering or sorting by price.");
        }

        return Result<PublicListingSearchFilters>.Success(new PublicListingSearchFilters(
            normalizedCountry?.ToUpperInvariant(),
            Normalize(city),
            Normalize(region),
            Normalize(district),
            minPrice,
            maxPrice,
            normalizedCurrency?.ToUpperInvariant(),
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

    private static Result<PublicListingSearchFilters> Fail(string description)
    {
        return Result<PublicListingSearchFilters>.Failure(PublicListingErrors.ValidationFailed(description));
    }
}
