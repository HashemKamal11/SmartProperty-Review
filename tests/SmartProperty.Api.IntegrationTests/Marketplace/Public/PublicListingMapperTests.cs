using System.Text.Json;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Public;

/// <summary>
/// The mapping from the Application public read models to the public response DTOs: exact field mapping, the rent
/// period rules, and failing closed on any corrupt read model.
/// </summary>
/// <remarks>
/// No host, no database, no endpoint: the mapper is a pure function.
/// </remarks>
public sealed class PublicListingMapperTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASaleSummaryMapsEveryFieldAndHasNoRentPeriod()
    {
        var response = PublicListingMapper.ToSummaryResponse(SaleSummary());

        Assert.Equal(ListingId, response.ListingId);
        Assert.Equal("Sale", response.Purpose);
        Assert.Equal("Corner villa", response.Title);
        Assert.Equal(new PublicMoneyResponse(1250000m, "SAR"), response.Price);
        Assert.Null(response.Price.RentPeriod);
        Assert.Equal("Building", response.PropertyType);
        Assert.Equal(new PublicLocationResponse("SA", "Riyadh", "Riyadh Province", "Al Malqa"), response.Location);
        Assert.Equal(PublishedAt, response.PublishedAt);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly, "Monthly")]
    [InlineData(RentPeriod.Yearly, "Yearly")]
    public void ARentSummaryMapsItsRentPeriodIntoThePrice(RentPeriod rentPeriod, string expected)
    {
        var response = PublicListingMapper.ToSummaryResponse(RentSummary(rentPeriod));

        Assert.Equal("Rent", response.Purpose);
        Assert.Equal(new PublicMoneyResponse(5200m, "SAR", expected), response.Price);
    }

    [Fact]
    public void ASaleDetailMapsEveryFieldIncludingTheDescription()
    {
        var response = PublicListingMapper.ToDetailResponse(SaleDetail());

        Assert.Equal(ListingId, response.ListingId);
        Assert.Equal("Sale", response.Purpose);
        Assert.Equal("Corner villa", response.Title);
        Assert.Equal("Quiet street, 450 square metres.", response.Description);
        Assert.Equal(new PublicMoneyResponse(1250000m, "SAR"), response.Price);
        Assert.Equal("Building", response.PropertyType);
        Assert.Equal(new PublicLocationResponse("SA", "Riyadh", "Riyadh Province", "Al Malqa"), response.Location);
        Assert.Equal(PublishedAt, response.PublishedAt);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly, "Monthly")]
    [InlineData(RentPeriod.Yearly, "Yearly")]
    public void ARentDetailMapsItsRentPeriodIntoThePrice(RentPeriod rentPeriod, string expected)
    {
        var response = PublicListingMapper.ToDetailResponse(RentDetail(rentPeriod));

        Assert.Equal("Rent", response.Purpose);
        Assert.Equal(new PublicMoneyResponse(5200m, "SAR", expected), response.Price);
    }

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Building, "Building")]
    [InlineData(PropertyType.Unit, "Unit")]
    public void EveryPropertyTypeMapsToItsPublicName(PropertyType propertyType, string expected)
    {
        var summary = PublicListingMapper.ToSummaryResponse(SaleSummary() with { PropertyType = propertyType });
        var detail = PublicListingMapper.ToDetailResponse(SaleDetail() with { PropertyType = propertyType });

        Assert.Equal(expected, summary.PropertyType);
        Assert.Equal(expected, detail.PropertyType);
    }

    [Fact]
    public void ASalePriceIsSerializedWithoutAnyRentPeriodMember()
    {
        var summaryJson = JsonSerializer.Serialize(PublicListingMapper.ToSummaryResponse(SaleSummary()), WebOptions);
        var detailJson = JsonSerializer.Serialize(PublicListingMapper.ToDetailResponse(SaleDetail()), WebOptions);

        Assert.DoesNotContain("rentPeriod", summaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rentPeriod", detailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARentPriceIsSerializedWithItsRentPeriod()
    {
        using var summary = Serialize(PublicListingMapper.ToSummaryResponse(RentSummary(RentPeriod.Yearly)));
        using var detail = Serialize(PublicListingMapper.ToDetailResponse(RentDetail(RentPeriod.Monthly)));

        Assert.Equal("Yearly", summary.RootElement.GetProperty("price").GetProperty("rentPeriod").GetString());
        Assert.Equal("Monthly", detail.RootElement.GetProperty("price").GetProperty("rentPeriod").GetString());
    }

    [Fact]
    public void TheSerializedSummaryHasExactlyTheApprovedFields()
    {
        using var document = Serialize(PublicListingMapper.ToSummaryResponse(RentSummary(RentPeriod.Monthly)));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root));
        Assert.Equal(new[] { "amount", "currency", "rentPeriod" }, SortedNames(root.GetProperty("price")));
        Assert.Equal(new[] { "city", "countryCode", "district", "region" }, SortedNames(root.GetProperty("location")));
    }

    [Fact]
    public void TheSerializedDetailHasExactlyTheApprovedFields()
    {
        using var document = Serialize(PublicListingMapper.ToDetailResponse(SaleDetail()));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "description", "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root));
        Assert.Equal(new[] { "amount", "currency" }, SortedNames(root.GetProperty("price")));
        Assert.Equal(new[] { "city", "countryCode", "district", "region" }, SortedNames(root.GetProperty("location")));
    }

    [Fact]
    public void ASaleListingWithARentPeriodIsRejected()
    {
        var summary = SaleSummary() with { Price = new PublicListingPrice(1250000m, "SAR", RentPeriod.Monthly) };
        var detail = SaleDetail() with { Price = new PublicListingPrice(1250000m, "SAR", RentPeriod.Yearly) };

        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(summary));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(detail));
    }

    [Fact]
    public void ARentListingWithoutARentPeriodIsRejected()
    {
        var summary = RentSummary(RentPeriod.Monthly) with { Price = new PublicListingPrice(5200m, "SAR", null) };
        var detail = RentDetail(RentPeriod.Monthly) with { Price = new PublicListingPrice(5200m, "SAR", null) };

        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(summary));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(detail));
    }

    [Fact]
    public void AnUndefinedRentPeriodIsRejected()
    {
        var summary = RentSummary((RentPeriod)99);
        var detail = RentDetail((RentPeriod)99);

        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(summary));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(detail));
    }

    [Fact]
    public void AnUndefinedPurposeIsRejected()
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Purpose = (ListingPurpose)99 }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Purpose = (ListingPurpose)99 }));
    }

    [Fact]
    public void AnUndefinedPropertyTypeIsRejected()
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { PropertyType = (PropertyType)99 }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { PropertyType = (PropertyType)99 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTitleIsRejected(string? title)
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Title = title! }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Title = title! }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankDescriptionIsRejected(string? description)
    {
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Description = description! }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankCurrencyIsRejected(string? currency)
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Price = new PublicListingPrice(1250000m, currency!, null) }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(RentDetail(RentPeriod.Monthly) with { Price = new PublicListingPrice(5200m, currency!, RentPeriod.Monthly) }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankCountryIsRejected(string? value)
    {
        AssertLocationRejected(new PublicListingLocation(value!, "Riyadh Province", "Riyadh", "Al Malqa"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankRegionIsRejected(string? value)
    {
        AssertLocationRejected(new PublicListingLocation("SA", value!, "Riyadh", "Al Malqa"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankCityIsRejected(string? value)
    {
        AssertLocationRejected(new PublicListingLocation("SA", "Riyadh Province", value!, "Al Malqa"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankDistrictIsRejected(string? value)
    {
        AssertLocationRejected(new PublicListingLocation("SA", "Riyadh Province", "Riyadh", value!));
    }

    [Fact]
    public void AMissingPriceOrLocationIsRejected()
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Price = null! }));
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Location = null! }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Price = null! }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Location = null! }));
    }

    [Fact]
    public void AnEmptyListingIdOrAMissingPublicationTimeIsRejected()
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { ListingId = Guid.Empty }));
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { PublishedAt = default }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { ListingId = Guid.Empty }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { PublishedAt = default }));
    }

    [Fact]
    public void ANullReadModelIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => PublicListingMapper.ToSummaryResponse(null!));
        Assert.Throws<ArgumentNullException>(() => PublicListingMapper.ToDetailResponse(null!));
    }

    [Fact]
    public void AZeroOrFractionalAmountIsMappedAsIsBecauseNoPriceRuleIsAddedHere()
    {
        var zero = PublicListingMapper.ToSummaryResponse(SaleSummary() with { Price = new PublicListingPrice(0m, "SAR", null) });
        var fractional = PublicListingMapper.ToSummaryResponse(SaleSummary() with { Price = new PublicListingPrice(1250000.55m, "SAR", null) });

        Assert.Equal(0m, zero.Price.Amount);
        Assert.Equal(1250000.55m, fractional.Price.Amount);
    }

    [Fact]
    public void ACorruptRowIsNeverSkippedWhenAPageIsMapped()
    {
        var page = new[] { SaleSummary(), SaleSummary() with { Title = " " }, SaleSummary() };

        AssertInvalid(() => page.Select(PublicListingMapper.ToSummaryResponse).ToArray());
    }

    [Fact]
    public void AFailureNeverEchoesAnyListingValue()
    {
        var corrupt = SaleSummary() with
        {
            Title = "SENTINEL-TITLE",
            Location = new PublicListingLocation("SA", "SENTINEL-REGION", "SENTINEL-CITY", " "),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PublicListingMapper.ToSummaryResponse(corrupt));

        Assert.DoesNotContain("SENTINEL", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ListingId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertLocationRejected(PublicListingLocation location)
    {
        AssertInvalid(() => PublicListingMapper.ToSummaryResponse(SaleSummary() with { Location = location }));
        AssertInvalid(() => PublicListingMapper.ToDetailResponse(SaleDetail() with { Location = location }));
    }

    private static void AssertInvalid(Action map)
    {
        var exception = Assert.Throws<InvalidOperationException>(map);

        Assert.StartsWith("A public listing read model is invalid:", exception.Message, StringComparison.Ordinal);
    }

    private static JsonDocument Serialize<T>(T value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value, WebOptions));
    }

    private static string[] SortedNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static PublicListingLocation Location()
    {
        return new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Malqa");
    }

    private static PublicListingSummary SaleSummary()
    {
        return new PublicListingSummary(
            ListingId,
            ListingPurpose.Sale,
            "Corner villa",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Building,
            Location(),
            PublishedAt);
    }

    private static PublicListingSummary RentSummary(RentPeriod rentPeriod)
    {
        return new PublicListingSummary(
            ListingId,
            ListingPurpose.Rent,
            "Bright apartment",
            new PublicListingPrice(5200m, "SAR", rentPeriod),
            PropertyType.Unit,
            Location(),
            PublishedAt);
    }

    private static PublicListingDetail SaleDetail()
    {
        return new PublicListingDetail(
            ListingId,
            ListingPurpose.Sale,
            "Corner villa",
            "Quiet street, 450 square metres.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Building,
            Location(),
            PublishedAt);
    }

    private static PublicListingDetail RentDetail(RentPeriod rentPeriod)
    {
        return new PublicListingDetail(
            ListingId,
            ListingPurpose.Rent,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", rentPeriod),
            PropertyType.Unit,
            Location(),
            PublishedAt);
    }
}
