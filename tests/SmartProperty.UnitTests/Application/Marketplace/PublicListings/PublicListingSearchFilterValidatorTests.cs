using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.PublicListings;

public sealed class PublicListingSearchFilterValidatorTests
{
    [Fact]
    public void NoFiltersIsValidSpansBothPurposesAndSortsNewest()
    {
        var result = Validate();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Purpose);
        Assert.Null(result.Value.PropertyType);
        Assert.Null(result.Value.CountryCode);
        Assert.Null(result.Value.Region);
        Assert.Null(result.Value.City);
        Assert.Null(result.Value.District);
        Assert.Null(result.Value.MinPrice);
        Assert.Null(result.Value.MaxPrice);
        Assert.Null(result.Value.Currency);
        Assert.Null(result.Value.RentPeriod);
        Assert.Equal(PublicListingSortOrder.Newest, result.Value.Sort);
    }

    [Theory]
    [InlineData("sa", "SA")]
    [InlineData("SA", "SA")]
    [InlineData("  sa  ", "SA")]
    public void TheCountryCodeIsTrimmedAndUpperCased(string input, string expected)
    {
        var result = Validate(countryCode: input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.CountryCode);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("SAU")]
    [InlineData("S1")]
    [InlineData("1S")]
    [InlineData("S-")]
    public void AnInvalidCountryCodeIsRejected(string input)
    {
        AssertValidationFailure(Validate(countryCode: input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCountryCodeMeansNoCountryFilter(string? input)
    {
        var result = Validate(countryCode: input);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.CountryCode);
    }

    [Fact]
    public void LocationFiltersAreTrimmedAndKeepTheirCase()
    {
        var result = Validate(city: "  Riyadh ", region: " Riyadh Province", district: "Al Olaya  ");

        Assert.True(result.IsSuccess);
        Assert.Equal("Riyadh", result.Value.City);
        Assert.Equal("Riyadh Province", result.Value.Region);
        Assert.Equal("Al Olaya", result.Value.District);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLocationFiltersMeanNoFilter(string input)
    {
        var result = Validate(city: input, region: input, district: input);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.City);
        Assert.Null(result.Value.Region);
        Assert.Null(result.Value.District);
    }

    [Theory]
    [InlineData("sar", "SAR")]
    [InlineData(" usd ", "USD")]
    [InlineData("EUR", "EUR")]
    public void TheCurrencyIsTrimmedAndUpperCased(string input, string expected)
    {
        var result = Validate(currency: input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Currency);
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("SARR")]
    [InlineData("S1R")]
    [InlineData("12A")]
    public void AnInvalidCurrencyIsRejected(string input)
    {
        AssertValidationFailure(Validate(currency: input));
    }

    [Fact]
    public void ACurrencyAloneIsAValidFilter()
    {
        var result = Validate(currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal("SAR", result.Value.Currency);
        Assert.Equal(PublicListingSortOrder.Newest, result.Value.Sort);
    }

    [Theory]
    [InlineData(PropertyType.Land)]
    [InlineData(PropertyType.Building)]
    [InlineData(PropertyType.Unit)]
    public void AValidPropertyTypePassesThrough(PropertyType propertyType)
    {
        var result = Validate(propertyType: propertyType);

        Assert.True(result.IsSuccess);
        Assert.Equal(propertyType, result.Value.PropertyType);
    }

    [Fact]
    public void AnUndefinedPropertyTypeIsRejected()
    {
        AssertValidationFailure(Validate(propertyType: (PropertyType)99));
    }

    [Fact]
    public void AnUndefinedPurposeIsRejected()
    {
        AssertValidationFailure(Validate(purpose: (ListingPurpose)99));
    }

    [Fact]
    public void AnUndefinedRentPeriodIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Rent, rentPeriod: (RentPeriod)99));
    }

    [Fact]
    public void AnUndefinedSortOrderIsRejected()
    {
        AssertValidationFailure(Validate(sort: (PublicListingSortOrder)99));
    }

    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void EachPurposeAloneIsAValidFilter(ListingPurpose purpose)
    {
        var result = Validate(purpose: purpose);

        Assert.True(result.IsSuccess);
        Assert.Equal(purpose, result.Value.Purpose);
    }

    [Fact]
    public void ANegativeMinimumPriceIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, minPrice: -0.01m, currency: "SAR"));
    }

    [Fact]
    public void ANegativeMaximumPriceIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, maxPrice: -1m, currency: "SAR"));
    }

    [Fact]
    public void AMinimumAboveTheMaximumIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, minPrice: 200m, maxPrice: 100m, currency: "SAR"));
    }

    [Fact]
    public void AnEqualMinimumAndMaximumIsAccepted()
    {
        var result = Validate(purpose: ListingPurpose.Sale, minPrice: 100m, maxPrice: 100m, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(100m, result.Value.MinPrice);
        Assert.Equal(100m, result.Value.MaxPrice);
    }

    [Fact]
    public void AZeroMinimumPriceIsAccepted()
    {
        var result = Validate(purpose: ListingPurpose.Sale, minPrice: 0m, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.MinPrice);
    }

    [Fact]
    public void AMinimumPriceWithoutACurrencyIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, minPrice: 100m));
    }

    [Fact]
    public void AMaximumPriceWithoutACurrencyIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, maxPrice: 100m));
    }

    [Fact]
    public void AMinimumPriceWithoutAPurposeIsRejected()
    {
        AssertValidationFailure(Validate(minPrice: 100m, currency: "SAR"));
    }

    [Fact]
    public void AMaximumPriceWithoutAPurposeIsRejected()
    {
        AssertValidationFailure(Validate(maxPrice: 100m, currency: "SAR"));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingByPriceWithoutACurrencyIsRejected(PublicListingSortOrder sort)
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, sort: sort));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingByPriceWithoutAPurposeIsRejected(PublicListingSortOrder sort)
    {
        AssertValidationFailure(Validate(sort: sort, currency: "SAR"));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingSalesByPriceWithACurrencyIsAccepted(PublicListingSortOrder sort)
    {
        var result = Validate(purpose: ListingPurpose.Sale, sort: sort, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(sort, result.Value.Sort);
        Assert.Null(result.Value.RentPeriod);
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingRentsByPriceWithoutARentPeriodIsRejected(PublicListingSortOrder sort)
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Rent, sort: sort, currency: "SAR"));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc, RentPeriod.Monthly)]
    [InlineData(PublicListingSortOrder.PriceDesc, RentPeriod.Yearly)]
    public void SortingRentsByPriceWithACurrencyAndARentPeriodIsAccepted(PublicListingSortOrder sort, RentPeriod rentPeriod)
    {
        var result = Validate(purpose: ListingPurpose.Rent, sort: sort, currency: "SAR", rentPeriod: rentPeriod);

        Assert.True(result.IsSuccess);
        Assert.Equal(sort, result.Value.Sort);
        Assert.Equal(rentPeriod, result.Value.RentPeriod);
    }

    [Fact]
    public void FilteringRentsByMinimumPriceWithoutARentPeriodIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Rent, minPrice: 1000m, currency: "SAR"));
    }

    [Fact]
    public void FilteringRentsByMaximumPriceWithoutARentPeriodIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Rent, maxPrice: 6000m, currency: "SAR"));
    }

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    public void FilteringRentsByPriceWithACurrencyAndARentPeriodIsAccepted(RentPeriod rentPeriod)
    {
        var result = Validate(purpose: ListingPurpose.Rent, minPrice: 1000m, maxPrice: 6000m, currency: "SAR", rentPeriod: rentPeriod);

        Assert.True(result.IsSuccess);
        Assert.Equal(rentPeriod, result.Value.RentPeriod);
    }

    [Fact]
    public void FilteringSalesByPriceNeedsNoRentPeriod()
    {
        var result = Validate(purpose: ListingPurpose.Sale, minPrice: 500000m, maxPrice: 2000000m, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.RentPeriod);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    public void ARentPeriodAloneWithTheRentPurposeIsAValidFilter(RentPeriod rentPeriod)
    {
        var result = Validate(purpose: ListingPurpose.Rent, rentPeriod: rentPeriod);

        Assert.True(result.IsSuccess);
        Assert.Equal(rentPeriod, result.Value.RentPeriod);
    }

    [Fact]
    public void ARentPeriodWithTheSalePurposeIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, rentPeriod: RentPeriod.Monthly));
    }

    [Fact]
    public void ARentPeriodWithoutAPurposeIsRejected()
    {
        AssertValidationFailure(Validate(rentPeriod: RentPeriod.Yearly));
    }

    [Fact]
    public void ARentPeriodWithASalePriceFilterIsRejected()
    {
        AssertValidationFailure(Validate(purpose: ListingPurpose.Sale, minPrice: 100m, currency: "SAR", rentPeriod: RentPeriod.Monthly));
    }

    [Fact]
    public void SortingByNewestNeedsNoCurrencyPurposeOrRentPeriod()
    {
        var result = Validate(sort: PublicListingSortOrder.Newest);

        Assert.True(result.IsSuccess);
        Assert.Equal(PublicListingSortOrder.Newest, result.Value.Sort);
    }

    [Fact]
    public void AFullyPopulatedValidFilterIsNormalisedAsAWhole()
    {
        var result = Validate(
            purpose: ListingPurpose.Rent,
            propertyType: PropertyType.Unit,
            countryCode: "sa",
            region: "Riyadh Province",
            city: " Riyadh ",
            district: "Al Olaya",
            minPrice: 1000m,
            maxPrice: 6000m,
            currency: "sar",
            rentPeriod: RentPeriod.Monthly,
            sort: PublicListingSortOrder.PriceAsc);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new PublicListingSearchCriteria(
                ListingPurpose.Rent,
                PropertyType.Unit,
                "SA",
                "Riyadh Province",
                "Riyadh",
                "Al Olaya",
                1000m,
                6000m,
                "SAR",
                RentPeriod.Monthly,
                PublicListingSortOrder.PriceAsc),
            result.Value);
    }

    [Fact]
    public void ValidationFailuresUseTheSharedValidationErrorShape()
    {
        var result = Validate(countryCode: "XXX");

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void TheNotFoundErrorIsTheStablePublicCode()
    {
        Assert.Equal("public_listings.not_found", PublicListingErrors.NotFound.Code);
        Assert.Equal(ErrorType.NotFound, PublicListingErrors.NotFound.Type);
    }

    private static Result<PublicListingSearchCriteria> Validate(
        ListingPurpose? purpose = null,
        PropertyType? propertyType = null,
        string? countryCode = null,
        string? region = null,
        string? city = null,
        string? district = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        string? currency = null,
        RentPeriod? rentPeriod = null,
        PublicListingSortOrder? sort = null)
    {
        return PublicListingSearchFilterValidator.Validate(
            purpose,
            propertyType,
            countryCode,
            region,
            city,
            district,
            minPrice,
            maxPrice,
            currency,
            rentPeriod,
            sort);
    }

    private static void AssertValidationFailure(Result<PublicListingSearchCriteria> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }
}
