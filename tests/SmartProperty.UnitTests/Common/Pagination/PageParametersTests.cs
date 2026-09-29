using SmartProperty.Common.Pagination;
using Xunit;

namespace SmartProperty.UnitTests.Common.Pagination;

public sealed class PageParametersTests
{
    [Fact]
    public void Defaults_AreTheFirstPageAndTenItems()
    {
        var parameters = new PageParameters();

        Assert.Equal(1, parameters.PageNumber);
        Assert.Equal(10, parameters.PageSize);
        Assert.Equal(0, parameters.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositivePage_NormalizesToOne(int page)
    {
        var parameters = new PageParameters(page, 10);

        Assert.Equal(1, parameters.PageNumber);
        Assert.Equal(0, parameters.Offset);
    }

    [Fact]
    public void IntMaxPage_NormalizesToThePracticalMaximum()
    {
        var parameters = new PageParameters(int.MaxValue, 100);

        Assert.Equal(PageParameters.MaxPageNumber, parameters.PageNumber);
        Assert.Equal(PageParameters.MaxOffset, parameters.Offset);
        Assert.True(parameters.Offset >= 0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(101, PageParameters.MaxPageSize)]
    [InlineData(int.MaxValue, PageParameters.MaxPageSize)]
    public void PageSize_NormalizesToItsSupportedRange(int pageSize, int expected)
    {
        var parameters = new PageParameters(1, pageSize);

        Assert.Equal(expected, parameters.PageSize);
    }

    [Fact]
    public void PagedList_NormalizesExtremeMetadataWithTheSameBounds()
    {
        var page = PagedList<int>.Create([], int.MaxValue, int.MaxValue, totalCount: 0);

        Assert.Equal(PageParameters.MaxPageNumber, page.PageNumber);
        Assert.Equal(PageParameters.MaxPageSize, page.PageSize);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void PagedList_AtThePageCeiling_HasNoReachableNextPage()
    {
        const long TotalCount = 2_000_000;

        var page = PagedList<int>.Create(
            [],
            PageParameters.MaxPageNumber,
            PageParameters.MaxPageSize,
            TotalCount);

        Assert.Equal(PageParameters.MaxPageNumber, page.PageNumber);
        Assert.Equal(PageParameters.MaxPageSize, page.PageSize);
        Assert.Equal(TotalCount, page.TotalCount);
        Assert.Equal(20_000, page.TotalPages);
        Assert.True(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void PagedList_BelowThePageCeiling_HasNextWhenAnotherPageIsReachable()
    {
        var page = PagedList<int>.Create([], pageNumber: 9, pageSize: 100, totalCount: 1_000);

        Assert.Equal(10, page.TotalPages);
        Assert.True(page.HasNextPage);
    }
}
