using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.Search;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.PublicListings;

public sealed class SearchPublicListingsQueryHandlerTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakePublicListingReadRepository _listings = new();

    [Fact]
    public async Task AbsentPageValuesDefaultToTheFirstPageOfTen()
    {
        var result = await Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PageNumber);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(0, _listings.LastSearchCall!.Skip);
        Assert.Equal(10, _listings.LastSearchCall.Take);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(int.MinValue, 1, 0)]
    [InlineData(3, 3, 20)]
    [InlineData(PageParameters.MaxPageNumber, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    [InlineData(PageParameters.MaxPageNumber + 1, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    [InlineData(int.MaxValue, PageParameters.MaxPageNumber, (PageParameters.MaxPageNumber - 1) * 10)]
    public async Task ThePageIsClamped(int page, int expectedPage, int expectedSkip)
    {
        var result = await Handle(Query(page: page));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPage, result.Value.PageNumber);
        Assert.Equal(expectedSkip, _listings.LastSearchCall!.Skip);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(25, 25)]
    [InlineData(PageParameters.MaxPageSize, PageParameters.MaxPageSize)]
    [InlineData(PageParameters.MaxPageSize + 1, PageParameters.MaxPageSize)]
    [InlineData(int.MaxValue, PageParameters.MaxPageSize)]
    public async Task ThePageSizeIsClamped(int pageSize, int expectedPageSize)
    {
        var result = await Handle(Query(pageSize: pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPageSize, result.Value.PageSize);
        Assert.Equal(expectedPageSize, _listings.LastSearchCall!.Take);
    }

    [Fact]
    public async Task TheSkipIsThePageOffsetForTheResolvedPageSize()
    {
        var result = await Handle(Query(page: 4, pageSize: 25));

        Assert.True(result.IsSuccess);
        Assert.Equal(75, _listings.LastSearchCall!.Skip);
        Assert.Equal(25, _listings.LastSearchCall.Take);
    }

    [Theory]
    [InlineData("XXX")]
    [InlineData("S")]
    public async Task AnInvalidCountryCodeIsRejectedWithoutCallingTheRepository(string countryCode)
    {
        var result = await Handle(Query(countryCode: countryCode));

        AssertValidationFailure(result);
        Assert.Equal(0, _listings.SearchCallCount);
    }

    [Fact]
    public async Task APriceFilterWithoutAPurposeIsRejectedWithoutCallingTheRepository()
    {
        var result = await Handle(Query(minPrice: 100m, currency: "SAR"));

        AssertValidationFailure(result);
        Assert.Equal(0, _listings.SearchCallCount);
    }

    [Fact]
    public async Task APriceFilterWithoutACurrencyIsRejectedWithoutCallingTheRepository()
    {
        var result = await Handle(Query(purpose: ListingPurpose.Sale, maxPrice: 100m));

        AssertValidationFailure(result);
        Assert.Equal(0, _listings.SearchCallCount);
    }

    [Fact]
    public async Task ARentPriceSortWithoutARentPeriodIsRejectedWithoutCallingTheRepository()
    {
        var result = await Handle(Query(purpose: ListingPurpose.Rent, currency: "SAR", sort: PublicListingSortOrder.PriceAsc));

        AssertValidationFailure(result);
        Assert.Equal(0, _listings.SearchCallCount);
    }

    [Fact]
    public async Task ARentPeriodWithASalePurposeIsRejectedWithoutCallingTheRepository()
    {
        var result = await Handle(Query(purpose: ListingPurpose.Sale, rentPeriod: RentPeriod.Yearly));

        AssertValidationFailure(result);
        Assert.Equal(0, _listings.SearchCallCount);
    }

    [Fact]
    public async Task NormalisedCriteriaReachTheRepositoryExactly()
    {
        var result = await Handle(Query(
            purpose: ListingPurpose.Rent,
            propertyType: PropertyType.Unit,
            countryCode: " sa ",
            region: " Riyadh Province ",
            city: "Riyadh",
            district: " Al Olaya",
            minPrice: 1000m,
            maxPrice: 6000m,
            currency: "sar",
            rentPeriod: RentPeriod.Monthly,
            sort: PublicListingSortOrder.PriceDesc,
            page: 2,
            pageSize: 20));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _listings.SearchCallCount);
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
                PublicListingSortOrder.PriceDesc),
            _listings.LastSearchCall!.Criteria);
        Assert.Equal(20, _listings.LastSearchCall.Skip);
        Assert.Equal(20, _listings.LastSearchCall.Take);
    }

    [Fact]
    public async Task ASaleSearchPassesNoRentPeriod()
    {
        var result = await Handle(Query(purpose: ListingPurpose.Sale, minPrice: 500000m, currency: "SAR"));

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingPurpose.Sale, _listings.LastSearchCall!.Criteria.Purpose);
        Assert.Null(_listings.LastSearchCall.Criteria.RentPeriod);
    }

    [Fact]
    public async Task ASearchWithoutAPurposeSpansBothPurposes()
    {
        var result = await Handle(Query(city: "Riyadh"));

        Assert.True(result.IsSuccess);
        Assert.Null(_listings.LastSearchCall!.Criteria.Purpose);
    }

    [Fact]
    public async Task TheDefaultSortIsNewest()
    {
        var result = await Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Equal(PublicListingSortOrder.Newest, _listings.LastSearchCall!.Criteria.Sort);
    }

    [Fact]
    public async Task TheRepositoryItemsAndTotalPassThroughUntouchedAndInOrder()
    {
        var first = Summary("Corner villa", ListingPurpose.Sale);
        var second = Summary("Bright apartment", ListingPurpose.Rent);
        var third = Summary("Studio", ListingPurpose.Rent);
        _listings.Page = new PublicListingPage([first, second, third], 25);

        var result = await Handle(Query(page: 2, pageSize: 3));

        Assert.True(result.IsSuccess);
        var page = result.Value;
        Assert.Equal(3, page.Items.Count);
        Assert.Same(first, page.Items[0]);
        Assert.Same(second, page.Items[1]);
        Assert.Same(third, page.Items[2]);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(9, page.TotalPages);
        Assert.True(page.HasPreviousPage);
        Assert.True(page.HasNextPage);
    }

    [Fact]
    public async Task AnEmptyRepositoryPageIsASuccessfulEmptyPage()
    {
        var result = await Handle(Query());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(0, result.Value.TotalPages);
        Assert.False(result.Value.HasNextPage);
        Assert.False(result.Value.HasPreviousPage);
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheRepository()
    {
        using var source = new CancellationTokenSource();

        await CreateHandler().Handle(Query(), source.Token);

        Assert.Equal(source.Token, _listings.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _listings.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Handle(Query()));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
    }

    private SearchPublicListingsQueryHandler CreateHandler()
    {
        return new SearchPublicListingsQueryHandler(_listings);
    }

    private Task<Result<PagedList<PublicListingSummary>>> Handle(SearchPublicListingsQuery query)
    {
        return CreateHandler().Handle(query);
    }

    private static SearchPublicListingsQuery Query(
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
        PublicListingSortOrder? sort = null,
        int? page = null,
        int? pageSize = null)
    {
        return new SearchPublicListingsQuery(
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
            sort,
            page,
            pageSize);
    }

    private static PublicListingSummary Summary(string title, ListingPurpose purpose)
    {
        var price = purpose == ListingPurpose.Rent
            ? new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly)
            : new PublicListingPrice(1250000m, "SAR", null);

        return new PublicListingSummary(
            Guid.NewGuid(),
            purpose,
            title,
            price,
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);
    }

    private static void AssertValidationFailure(Result<PagedList<PublicListingSummary>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }
}
