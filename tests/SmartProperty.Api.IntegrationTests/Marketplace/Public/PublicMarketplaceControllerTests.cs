using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Api.Controllers;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.GetById;
using SmartProperty.Application.Marketplace.PublicListings.Search;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Public;

/// <summary>
/// The public marketplace controller on its own: no host, no database. It checks the routing and anonymity metadata
/// and calls the actions directly with fake handlers.
/// </summary>
public sealed class PublicMarketplaceControllerTests
{
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeSearchHandler _search = new();
    private readonly FakeDetailHandler _detail = new();

    [Fact]
    public void TheControllerIsAnAnonymousApiControllerOnThePublicRoute()
    {
        var type = typeof(PublicMarketplaceController);

        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/public/marketplace/listings", type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void NoAuthorizationRequirementIsDeclaredAnywhereOnTheController()
    {
        var type = typeof(PublicMarketplaceController);

        Assert.Empty(type.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>());

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Empty(method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>());
        }
    }

    [Fact]
    public void TheTwoReadsAreGetRoutesAndNothingElseIsExposed()
    {
        var methods = typeof(PublicMarketplaceController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "GetById", "List" }, methods.Select(method => method.Name).ToArray());

        var getById = methods.Single(method => method.Name == "GetById").GetCustomAttribute<HttpGetAttribute>()!;
        var list = methods.Single(method => method.Name == "List").GetCustomAttribute<HttpGetAttribute>()!;

        Assert.Equal("{listingId:guid}", getById.Template);
        Assert.Null(list.Template);
        Assert.All(methods, method => Assert.DoesNotContain(method.GetCustomAttributes().OfType<HttpMethodAttribute>(), attribute => attribute is not HttpGetAttribute));
    }

    [Fact]
    public void TheSearchAcceptsExactlyTheApprovedFilters()
    {
        var parameters = typeof(PublicMarketplaceController).GetMethod("List")!
            .GetParameters()
            .Select(parameter => parameter.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "cancellationToken", "city", "countryCode", "currency", "district", "handler", "maxPrice", "minPrice",
                "page", "pageSize", "propertyType", "purpose", "region", "rentPeriod", "sort",
            },
            parameters);
    }

    [Fact]
    public async Task ASuccessfulSearchReturns200WithTheMappedPageAndItsMetadata()
    {
        var sale = Summary(ListingPurpose.Sale);
        var rent = Summary(ListingPurpose.Rent);
        _search.Outcome = Result<PagedList<PublicListingSummary>>.Success(
            PagedList<PublicListingSummary>.Create([sale, rent], pageNumber: 2, pageSize: 3, totalCount: 25));

        var result = await CreateController().List(_search, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<PagedResponse<PublicListingSummaryResponse>>(ok.Value);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Sale", page.Items[0].Purpose);
        Assert.Null(page.Items[0].Price.RentPeriod);
        Assert.Equal("Rent", page.Items[1].Purpose);
        Assert.Equal("Monthly", page.Items[1].Price.RentPeriod);
        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(9, page.TotalPages);
        Assert.True(page.HasNext);
        Assert.True(page.HasPrevious);
    }

    [Fact]
    public async Task AnEmptySearchReturns200WithAnEmptyPage()
    {
        var result = await CreateController().List(_search, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<PagedResponse<PublicListingSummaryResponse>>(ok.Value);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task EveryQueryParameterReachesTheSearchQueryUnchanged()
    {
        await CreateController().List(
            _search,
            CancellationToken.None,
            purpose: ListingPurpose.Rent,
            propertyType: PropertyType.Unit,
            countryCode: "sa",
            region: "Riyadh Province",
            city: "Riyadh",
            district: "Al Olaya",
            minPrice: 1000m,
            maxPrice: 6000m,
            currency: "sar",
            rentPeriod: RentPeriod.Monthly,
            sort: PublicListingSortOrder.PriceAsc,
            page: 2,
            pageSize: 20);

        Assert.Equal(1, _search.CallCount);
        Assert.Equal(
            new SearchPublicListingsQuery(
                ListingPurpose.Rent,
                PropertyType.Unit,
                "sa",
                "Riyadh Province",
                "Riyadh",
                "Al Olaya",
                1000m,
                6000m,
                "sar",
                RentPeriod.Monthly,
                PublicListingSortOrder.PriceAsc,
                2,
                20),
            _search.LastQuery);
    }

    [Fact]
    public async Task AValidationFailureIsA422WithTheSharedErrorBody()
    {
        _search.Outcome = Result<PagedList<PublicListingSummary>>.Failure(
            new Error("validation.failed", "A currency is required when filtering or sorting by price.", ErrorType.Validation));

        var result = await CreateController().List(_search, CancellationToken.None, minPrice: 100m);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(response.Value);
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, error.Status);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
    }

    [Fact]
    public async Task TheSearchCancellationTokenReachesTheHandler()
    {
        using var source = new CancellationTokenSource();

        await CreateController().List(_search, source.Token);

        Assert.Equal(source.Token, _search.Token);
    }

    [Fact]
    public async Task ACorruptReadModelIsNeverSkippedOrPartiallyShown()
    {
        var corrupt = Summary(ListingPurpose.Sale) with { Title = " " };
        _search.Outcome = Result<PagedList<PublicListingSummary>>.Success(
            PagedList<PublicListingSummary>.Create([Summary(ListingPurpose.Sale), corrupt], 1, 10, 2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController().List(_search, CancellationToken.None));
    }

    [Fact]
    public async Task AFoundListingReturns200WithTheMappedDetail()
    {
        _detail.Outcome = Result<PublicListingDetail>.Success(Detail(ListingPurpose.Rent));

        var result = await CreateController().GetById(ListingId, _detail, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var detail = Assert.IsType<PublicListingDetailResponse>(ok.Value);
        Assert.Equal(ListingId, detail.ListingId);
        Assert.Equal("Rent", detail.Purpose);
        Assert.Equal("Two bedrooms close to the centre.", detail.Description);
        Assert.Equal(new PublicMoneyResponse(5200m, "SAR", "Monthly"), detail.Price);
        Assert.Equal(ListingId, _detail.LastQuery!.ListingId);
    }

    [Fact]
    public async Task ASaleDetailHasNoRentPeriod()
    {
        _detail.Outcome = Result<PublicListingDetail>.Success(Detail(ListingPurpose.Sale));

        var result = await CreateController().GetById(ListingId, _detail, CancellationToken.None);

        var detail = Assert.IsType<PublicListingDetailResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(detail.Price.RentPeriod);
    }

    [Fact]
    public async Task ANotFoundListingIsA404WithTheStablePublicCode()
    {
        _detail.Outcome = Result<PublicListingDetail>.Failure(
            new Error("public_listings.not_found", "The listing was not found.", ErrorType.NotFound));

        var result = await CreateController().GetById(ListingId, _detail, CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(response.Value);
        Assert.Equal("public_listings.not_found", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
    }

    [Fact]
    public async Task TheDetailCancellationTokenReachesTheHandler()
    {
        using var source = new CancellationTokenSource();
        _detail.Outcome = Result<PublicListingDetail>.Success(Detail(ListingPurpose.Sale));

        await CreateController().GetById(ListingId, _detail, source.Token);

        Assert.Equal(source.Token, _detail.Token);
    }

    [Fact]
    public async Task ACorruptDetailIsNeverShown()
    {
        _detail.Outcome = Result<PublicListingDetail>.Success(Detail(ListingPurpose.Rent) with { Price = new PublicListingPrice(5200m, "SAR", null) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController().GetById(ListingId, _detail, CancellationToken.None));
    }

    private static PublicMarketplaceController CreateController()
    {
        return new PublicMarketplaceController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static PublicListingSummary Summary(ListingPurpose purpose)
    {
        return new PublicListingSummary(
            ListingId,
            purpose,
            purpose == ListingPurpose.Rent ? "Bright apartment" : "Corner villa",
            Price(purpose),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);
    }

    private static PublicListingDetail Detail(ListingPurpose purpose)
    {
        return new PublicListingDetail(
            ListingId,
            purpose,
            purpose == ListingPurpose.Rent ? "Bright apartment" : "Corner villa",
            purpose == ListingPurpose.Rent ? "Two bedrooms close to the centre." : "Quiet street, 450 square metres.",
            Price(purpose),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);
    }

    private static PublicListingPrice Price(ListingPurpose purpose)
    {
        return purpose == ListingPurpose.Rent
            ? new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly)
            : new PublicListingPrice(1250000m, "SAR", null);
    }

    private sealed class FakeSearchHandler : IQueryHandler<SearchPublicListingsQuery, PagedList<PublicListingSummary>>
    {
        public Result<PagedList<PublicListingSummary>> Outcome { get; set; } =
            Result<PagedList<PublicListingSummary>>.Success(PagedList<PublicListingSummary>.Create([], 1, 10, 0));

        public int CallCount { get; private set; }

        public SearchPublicListingsQuery? LastQuery { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<Result<PagedList<PublicListingSummary>>> Handle(
            SearchPublicListingsQuery query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastQuery = query;
            Token = cancellationToken;

            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDetailHandler : IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail>
    {
        public Result<PublicListingDetail> Outcome { get; set; } =
            Result<PublicListingDetail>.Failure(new Error("public_listings.not_found", "The listing was not found.", ErrorType.NotFound));

        public GetPublicListingByIdQuery? LastQuery { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<Result<PublicListingDetail>> Handle(
            GetPublicListingByIdQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            Token = cancellationToken;

            return Task.FromResult(Outcome);
        }
    }
}
