using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class GetPropertiesQueryHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 4, 8, 30, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();

    [Fact]
    public async Task AbsentPageValuesDefaultToTheFirstPageOfTen()
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PageNumber);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(0, _properties.LastListCall!.Skip);
        Assert.Equal(10, _properties.LastListCall.Take);
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
        var result = await Handle(new GetPropertiesQuery(null, null, null, page, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPage, result.Value.PageNumber);
        Assert.Equal(expectedSkip, _properties.LastListCall!.Skip);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(25, 25)]
    [InlineData(PageParameters.MaxPageSize, PageParameters.MaxPageSize)]
    [InlineData(1000, PageParameters.MaxPageSize)]
    [InlineData(int.MaxValue, PageParameters.MaxPageSize)]
    public async Task ThePageSizeIsClamped(int pageSize, int expectedPageSize)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, pageSize));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPageSize, result.Value.PageSize);
        Assert.Equal(expectedPageSize, _properties.LastListCall!.Take);
    }

    [Fact]
    public async Task TheMaximumPageAndPageSizeProduceTheMaximumOffset()
    {
        await Handle(new GetPropertiesQuery(null, null, null, int.MaxValue, int.MaxValue));

        Assert.Equal(PageParameters.MaxOffset, _properties.LastListCall!.Skip);
        Assert.Equal(PageParameters.MaxPageSize, _properties.LastListCall.Take);
    }

    [Fact]
    public async Task PageMetadataComesFromTheRepositoryTotal()
    {
        _properties.Page = new PropertyPage([Details(), Details()], 25);

        var result = await Handle(new GetPropertiesQuery(null, null, null, 2, 10));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(2, result.Value.PageNumber);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(25, result.Value.TotalCount);
        Assert.Equal(3, result.Value.TotalPages);
        Assert.True(result.Value.HasPreviousPage);
        Assert.True(result.Value.HasNextPage);
    }

    [Theory]
    [InlineData(PropertyType.Land)]
    [InlineData(PropertyType.Building)]
    [InlineData(PropertyType.Apartment)]
    public async Task TheTypeFilterIsForwarded(PropertyType type)
    {
        await Handle(new GetPropertiesQuery([type], null, null, null, null));

        Assert.Equal(type, _properties.LastListCall!.Type);
    }

    [Theory]
    [InlineData(PropertyStatus.Active)]
    [InlineData(PropertyStatus.Archived)]
    public async Task TheStatusFilterIsForwarded(PropertyStatus status)
    {
        await Handle(new GetPropertiesQuery(null, status, null, null, null));

        Assert.Equal(status, _properties.LastListCall!.Status);
    }

    [Fact]
    public async Task ANullStatusListsActivePropertiesOnly()
    {
        await Handle(new GetPropertiesQuery(null, null, null, null, null));

        // Archived properties stay in the registry but are listed only when explicitly asked for.
        Assert.Equal(PropertyStatus.Active, _properties.LastListCall!.Status);
        Assert.Null(_properties.LastListCall.Type);
        Assert.Null(_properties.LastListCall.CountryCode);
    }

    [Theory]
    [InlineData(" eg ", "EG")]
    [InlineData("eg", "EG")]
    [InlineData("Sa", "SA")]
    [InlineData("SA", "SA")]
    public async Task TheCountryCodeIsTrimmedAndUpperCased(string countryCode, string expected)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, countryCode, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, _properties.LastListCall!.CountryCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankCountryCodeIsNoFilter(string? countryCode)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, countryCode, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _properties.ListCallCount);
        Assert.Null(_properties.LastListCall!.CountryCode);
    }

    [Theory]
    [InlineData("E1")]
    [InlineData("EGY")]
    [InlineData("E")]
    [InlineData("É G")]
    [InlineData("ÉG")]
    [InlineData("مص")]
    [InlineData("E G")]
    public async Task AnInvalidCountryCodeIsAValidationFailureThatNeverReachesTheRepository(string countryCode)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, countryCode, null, null));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal("Country code must contain exactly two ASCII letters.", result.Error.Description);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Fact]
    public async Task TheRepositoryOrderingIsPreserved()
    {
        var first = Details(CreatedAt.AddMinutes(2));
        var second = Details(CreatedAt.AddMinutes(1));
        var third = Details(CreatedAt);
        _properties.Page = new PropertyPage([first, second, third], 3);

        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null));

        Assert.Equal(new[] { first, second, third }, result.Value.Items);
    }

    [Fact]
    public async Task EveryFilterAndTheCancellationTokenAreForwardedTogether()
    {
        using var cancellation = new CancellationTokenSource();

        await CreateHandler().Handle(
            new GetPropertiesQuery([PropertyType.Apartment], PropertyStatus.Archived, " eg ", 2, 5),
            cancellation.Token);

        Assert.Equal(1, _properties.ListCallCount);
        Assert.Equal(
            new FakePropertyRepository.ListCall(PropertyType.Apartment, PropertyStatus.Archived, "EG", 5, 5),
            _properties.LastListCall);
        Assert.Equal(cancellation.Token, _properties.CancellationToken);
    }

    [Fact]
    public async Task MultipleTypesDistrictAreaPriceAndBedroomsAreForwardedNormalized()
    {
        var areaId = Guid.NewGuid();

        var result = await Handle(new GetPropertiesQuery(
            [PropertyType.Villa, PropertyType.Apartment, PropertyType.Villa], null, null, null, null,
            District: "  Hittin ", AreaId: areaId, MinPrice: 1000m, MaxPrice: 3200000m, MinBedrooms: 4, MaxBedrooms: 4));

        Assert.True(result.IsSuccess);
        var filter = _properties.LastSearchFilter!;
        Assert.Equal([PropertyType.Villa, PropertyType.Apartment], filter.Types);
        Assert.Equal("Hittin", filter.District);
        Assert.Equal(areaId, filter.AreaId);
        Assert.Equal(1000m, filter.MinPrice);
        Assert.Equal(3200000m, filter.MaxPrice);
        Assert.Equal(4, filter.MinBedrooms);
        Assert.Equal(4, filter.MaxBedrooms);
        Assert.Null(filter.MatchUserId);
    }

    [Fact]
    public async Task SearchTextIsSplitIntoDistinctTermsAndSortDefaultsToRelevance()
    {
        await Handle(new GetPropertiesQuery(null, null, null, null, null, Search: "  villa   Hittin VILLA "));

        var filter = _properties.LastSearchFilter!;
        Assert.Equal(["villa", "Hittin"], filter.SearchTerms);
        Assert.Equal(PropertyListSortOrder.Relevance, filter.Sort);
        Assert.Null(filter.ListingStatus);
    }

    [Theory]
    [InlineData(PropertyListSortOrder.Newest)]
    [InlineData(PropertyListSortOrder.PriceAsc)]
    [InlineData(PropertyListSortOrder.PriceDesc)]
    public async Task TheSortOrderAndListingStatusAreForwarded(PropertyListSortOrder sort)
    {
        await Handle(new GetPropertiesQuery(null, null, null, null, null, ListingStatus: ListingStatus.Sold, Sort: sort));

        Assert.Equal(sort, _properties.LastSearchFilter!.Sort);
        Assert.Equal(ListingStatus.Sold, _properties.LastSearchFilter.ListingStatus);
    }

    [Fact]
    public async Task ExplicitlyAskingForArchivedIsHonoured()
    {
        await Handle(new GetPropertiesQuery(null, PropertyStatus.Archived, null, null, null));

        Assert.Equal(PropertyStatus.Archived, _properties.LastSearchFilter!.Status);
    }

    [Fact]
    public async Task SearchTextOverTheLengthOrWordLimitIsAValidationFailure()
    {
        var tooLong = await Handle(new GetPropertiesQuery(null, null, null, null, null, Search: new string('a', 101)));
        var tooManyWords = await Handle(new GetPropertiesQuery(null, null, null, null, null, Search: "a b c d e f"));

        Assert.True(tooLong.IsFailure);
        Assert.True(tooManyWords.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Fact]
    public async Task AnUnsupportedSortOrAListingStatusIsAValidationFailure()
    {
        var sort = await Handle(new GetPropertiesQuery(null, null, null, null, null, Sort: (PropertyListSortOrder)99));
        var listing = await Handle(new GetPropertiesQuery(null, null, null, null, null, ListingStatus: (ListingStatus)99));

        Assert.True(sort.IsFailure);
        Assert.True(listing.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Fact]
    public async Task TheMatchFilterIsForwardedWithTheSignedInUser()
    {
        var userId = Guid.NewGuid();

        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null, MinMatchPercentage: 85m, MatchUserId: userId));

        Assert.True(result.IsSuccess);
        Assert.Equal(85m, _properties.LastSearchFilter!.MinMatchPercentage);
        Assert.Equal(userId, _properties.LastSearchFilter.MatchUserId);
    }

    [Fact]
    public async Task TheMatchFilterWithoutASignedInUserIsAValidationFailure()
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null, MinMatchPercentage: 85m));

        Assert.True(result.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    [InlineData(2000, 1000)]
    public async Task ANegativeOrInvertedPriceRangeIsAValidationFailure(int? min, int? max)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null, MinPrice: min, MaxPrice: max));

        Assert.True(result.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 101)]
    [InlineData(5, 4)]
    public async Task AnOutOfRangeOrInvertedBedroomRangeIsAValidationFailure(int? min, int? max)
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null, MinBedrooms: min, MaxBedrooms: max));

        Assert.True(result.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public async Task AnOutOfRangeMatchPercentageIsAValidationFailure(double percentage)
    {
        var result = await Handle(new GetPropertiesQuery(
            null, null, null, null, null, MinMatchPercentage: (decimal)percentage, MatchUserId: Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Fact]
    public async Task AnEmptyAreaIdIsAValidationFailure()
    {
        var result = await Handle(new GetPropertiesQuery(null, null, null, null, null, AreaId: Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal(0, _properties.ListCallCount);
    }

    [Fact]
    public async Task ANullQueryThrows()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
        Assert.Equal(0, _properties.ListCallCount);
    }

    private Task<Result<PagedList<PropertyDetails>>> Handle(GetPropertiesQuery query)
    {
        return CreateHandler().Handle(query);
    }

    private GetPropertiesQueryHandler CreateHandler()
    {
        return new GetPropertiesQueryHandler(_properties);
    }

    private static PropertyDetails Details(DateTimeOffset? createdAt = null)
    {
        var at = createdAt ?? CreatedAt;

        return new PropertyDetails(
            Guid.NewGuid(),
            PropertyType.Land,
            PropertyStatus.Active,
            new PropertyAddressDetails("SA", null, null, null, null, null, null, null),
            at,
            at);
    }
}
