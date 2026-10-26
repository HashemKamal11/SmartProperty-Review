using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Results;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.PublicListings;

public sealed class PublicListingSearchFilterValidatorTests
{
    [Fact]
    public void NoFiltersIsValidAndSortsNewest()
    {
        var result = Validate();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.CountryCode);
        Assert.Null(result.Value.City);
        Assert.Null(result.Value.Region);
        Assert.Null(result.Value.District);
        Assert.Null(result.Value.MinPrice);
        Assert.Null(result.Value.MaxPrice);
        Assert.Null(result.Value.Currency);
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

    [Fact]
    public void ANegativeMinimumPriceIsRejected()
    {
        AssertValidationFailure(Validate(minPrice: -0.01m, currency: "SAR"));
    }

    [Fact]
    public void ANegativeMaximumPriceIsRejected()
    {
        AssertValidationFailure(Validate(maxPrice: -1m, currency: "SAR"));
    }

    [Fact]
    public void AMinimumAboveTheMaximumIsRejected()
    {
        AssertValidationFailure(Validate(minPrice: 200m, maxPrice: 100m, currency: "SAR"));
    }

    [Fact]
    public void AnEqualMinimumAndMaximumIsAccepted()
    {
        var result = Validate(minPrice: 100m, maxPrice: 100m, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(100m, result.Value.MinPrice);
        Assert.Equal(100m, result.Value.MaxPrice);
    }

    [Fact]
    public void AZeroMinimumPriceIsAccepted()
    {
        var result = Validate(minPrice: 0m, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.MinPrice);
    }

    [Fact]
    public void AMinimumPriceWithoutACurrencyIsRejected()
    {
        AssertValidationFailure(Validate(minPrice: 100m));
    }

    [Fact]
    public void AMaximumPriceWithoutACurrencyIsRejected()
    {
        AssertValidationFailure(Validate(maxPrice: 100m));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingByPriceWithoutACurrencyIsRejected(PublicListingSortOrder sort)
    {
        AssertValidationFailure(Validate(sort: sort));
    }

    [Theory]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void SortingByPriceWithACurrencyIsAccepted(PublicListingSortOrder sort)
    {
        var result = Validate(sort: sort, currency: "SAR");

        Assert.True(result.IsSuccess);
        Assert.Equal(sort, result.Value.Sort);
    }

    [Fact]
    public void SortingByNewestNeedsNoCurrency()
    {
        var result = Validate(sort: PublicListingSortOrder.Newest);

        Assert.True(result.IsSuccess);
        Assert.Equal(PublicListingSortOrder.Newest, result.Value.Sort);
    }

    [Fact]
    public void AnUndefinedSortOrderIsRejected()
    {
        AssertValidationFailure(Validate(sort: (PublicListingSortOrder)99));
    }

    [Fact]
    public void AFullyPopulatedValidFilterIsNormalisedAsAWhole()
    {
        var result = Validate(
            countryCode: "sa",
            city: " Riyadh ",
            region: "Riyadh Province",
            district: "Al Olaya",
            minPrice: 1000m,
            maxPrice: 6000m,
            currency: "sar",
            sort: PublicListingSortOrder.PriceAsc);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new PublicListingSearchFilters("SA", "Riyadh", "Riyadh Province", "Al Olaya", 1000m, 6000m, "SAR", PublicListingSortOrder.PriceAsc),
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

    private static Result<PublicListingSearchFilters> Validate(
        string? countryCode = null,
        string? city = null,
        string? region = null,
        string? district = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        string? currency = null,
        PublicListingSortOrder? sort = null)
    {
        return PublicListingSearchFilterValidator.Validate(
            countryCode,
            city,
            region,
            district,
            minPrice,
            maxPrice,
            currency,
            sort);
    }

    private static void AssertValidationFailure(Result<PublicListingSearchFilters> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }
}
