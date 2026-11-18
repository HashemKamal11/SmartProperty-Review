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
        PublicListingSortOrder? sort,
        DateOnly? availableFrom = null,
        DateOnly? availableTo = null,
        IReadOnlyList<PropertyType>? propertyTypes = null,
        string? search = null,
        Guid? areaId = null,
        Guid? cityId = null,
        Guid? compoundId = null,
        int? minBedrooms = null,
        int? maxBedrooms = null,
        decimal? minMatchPercentage = null,
        Guid? viewerUserId = null)
    {
        // The single type and the list are one "any of" set.
        var types = (propertyTypes ?? []).Concat(propertyType is { } single ? [single] : []).Distinct().ToArray();

        if (types.Any(candidate => !Enum.IsDefined(candidate)))
        {
            return Fail("The property type is not supported.");
        }

        string[] searchTerms = [];

        if (Normalize(search) is { } text)
        {
            if (text.Length > MaxSearchLength)
            {
                return Fail($"Search text must not exceed {MaxSearchLength} characters.");
            }

            searchTerms = text
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (searchTerms.Length > MaxSearchTerms)
            {
                return Fail($"Search text must not exceed {MaxSearchTerms} words.");
            }
        }

        if (areaId == Guid.Empty || cityId == Guid.Empty || compoundId == Guid.Empty)
        {
            return Fail("A place id must not be empty.");
        }

        if (minBedrooms is < 0 or > Property.MaxRoomCount || maxBedrooms is < 0 or > Property.MaxRoomCount)
        {
            return Fail($"Bedrooms must be between 0 and {Property.MaxRoomCount}.");
        }

        if (minBedrooms is { } fewest && maxBedrooms is { } most && fewest > most)
        {
            return Fail("The minimum bedrooms must not exceed the maximum bedrooms.");
        }

        var viewer = viewerUserId is { } signedIn && signedIn != Guid.Empty ? signedIn : (Guid?)null;

        if (minMatchPercentage is { } minMatch)
        {
            if (minMatch is < 0m or > 100m)
            {
                return Fail("Match percentage must be between 0 and 100.");
            }

            if (viewer is null)
            {
                return Fail("Sign in to filter by match percentage.");
            }
        }

        if (sort == PublicListingSortOrder.Match && viewer is null)
        {
            return Fail("Sign in to sort by match percentage.");
        }

        if (purpose is { } listingPurpose && !Enum.IsDefined(listingPurpose))
        {
            return Fail("The listing purpose is not supported.");
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

        // Availability is a stay: both dates, the start before the end, for a Rent search, and not longer than a rental
        // may be. A Sale has no dates, so asking for them with another purpose is a mistake, not a filter that matches
        // nothing.
        if (availableFrom is not null || availableTo is not null)
        {
            if (availableFrom is null || availableTo is null)
            {
                return Fail("Availability needs both a start date and an end date.");
            }

            if (availableTo <= availableFrom)
            {
                return Fail("The availability end date must be after the start date.");
            }

            if (availableTo.Value.DayNumber - availableFrom.Value.DayNumber > 3650)
            {
                return Fail("The availability period is longer than a rental can be.");
            }

            if (purpose != ListingPurpose.Rent)
            {
                return Fail("Availability dates are valid only for Rent listings.");
            }
        }

        return Result<PublicListingSearchCriteria>.Success(new PublicListingSearchCriteria(
            purpose,
            types.Length == 1 ? types[0] : null,
            normalizedCountry?.ToUpperInvariant(),
            Normalize(region),
            Normalize(city),
            Normalize(district),
            minPrice,
            maxPrice,
            normalizedCurrency?.ToUpperInvariant(),
            rentPeriod,
            sortOrder,
            availableFrom,
            availableTo,
            // One type travels as the single filter; only a real choice of several becomes the list.
            types.Length > 1 ? types : null,
            searchTerms.Length > 0 ? searchTerms : null,
            areaId,
            cityId,
            compoundId,
            minBedrooms,
            maxBedrooms,
            minMatchPercentage,
            viewer));
    }

    public const int MaxSearchLength = 100;
    public const int MaxSearchTerms = 5;

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
