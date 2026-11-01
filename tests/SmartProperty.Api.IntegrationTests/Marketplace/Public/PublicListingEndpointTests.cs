using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.GetById;
using SmartProperty.Application.Marketplace.PublicListings.Search;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Public;

/// <summary>
/// The public marketplace routes through the real host, with a test-only stub in place of the PostgreSQL read
/// repository.
/// </summary>
/// <remarks>
/// These tests prove how the API presents what a repository returns: anonymity, routing, binding, validation, error
/// bodies and the public JSON shape. They deliberately say nothing about whether the real database query is correct;
/// that is covered by the PostgreSQL-backed repository tests. The host needs no database: nothing here opens a
/// connection, and the connection string points nowhere.
/// </remarks>
public sealed class PublicListingEndpointTests
{
    private const string ListRoute = "/api/public/marketplace/listings";
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] PrivateTerms =
    [
        "propertyId", "submission", "evidence", "owner", "workspace", "reviewer", "review", "addressLine",
        "postalCode", "latitude", "longitude", "permission", "status",
    ];

    [Fact]
    public async Task AnAnonymousSearchReturns200WithTheApprovedPublicShape()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage([Summary(ListingPurpose.Sale), Summary(ListingPurpose.Rent, RentPeriod.Yearly)], 2);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            new[] { "hasNext", "hasPrevious", "items", "page", "pageSize", "totalCount", "totalPages" },
            SortedNames(root));
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, root.GetProperty("totalCount").GetInt64());

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var sale = items[0];
        Assert.Equal(
            new[] { "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(sale));
        Assert.Equal("Sale", sale.GetProperty("purpose").GetString());
        Assert.Equal(new[] { "amount", "currency" }, SortedNames(sale.GetProperty("price")));
        Assert.Equal(new[] { "city", "countryCode", "district", "region" }, SortedNames(sale.GetProperty("location")));

        var rent = items[1];
        Assert.Equal("Rent", rent.GetProperty("purpose").GetString());
        Assert.Equal(new[] { "amount", "currency", "rentPeriod" }, SortedNames(rent.GetProperty("price")));
        Assert.Equal("Yearly", rent.GetProperty("price").GetProperty("rentPeriod").GetString());
    }

    [Fact]
    public async Task ASaleListingNeverCarriesARentPeriodMemberInTheRawJson()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage([Summary(ListingPurpose.Sale)], 1);
        using var client = host.CreateClient();

        var body = await client.GetStringAsync(ListRoute);

        Assert.DoesNotContain("rentPeriod", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AValidBearerTokenChangesNothingOnAPublicRead()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage([Summary(ListingPurpose.Sale)], 1);
        using var anonymous = host.CreateClient();
        using var authenticated = host.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.CreateAccessToken(Guid.NewGuid()));

        var anonymousBody = await anonymous.GetStringAsync(ListRoute);
        var authenticatedBody = await authenticated.GetStringAsync(ListRoute);

        Assert.Equal(anonymousBody, authenticatedBody);
    }

    [Fact]
    public async Task AnExpiredBearerTokenDoesNotBlockAnAnonymousRead()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.CreateExpiredAccessToken(Guid.NewGuid()));

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EveryFilterReachesTheRepositoryNormalisedAndPagedOnTheServer()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            $"{ListRoute}?purpose=Rent&propertyType=Unit&countryCode=sa&region=%20Riyadh%20Province&city=Riyadh"
            + "&district=Al%20Olaya&minPrice=1000&maxPrice=6000&currency=sar&rentPeriod=Monthly&sort=PriceAsc&page=2&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Stub.SearchCallCount);
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
            host.Stub.LastSearchCall!.Criteria);
        Assert.Equal(20, host.Stub.LastSearchCall.Skip);
        Assert.Equal(20, host.Stub.LastSearchCall.Take);
    }

    [Fact]
    public async Task ASearchWithoutAPurposeSpansBothPurposesAndSortsNewest()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}?city=Riyadh");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(host.Stub.LastSearchCall!.Criteria.Purpose);
        Assert.Equal(PublicListingSortOrder.Newest, host.Stub.LastSearchCall.Criteria.Sort);
    }

    [Fact]
    public async Task ThePageSizeIsClampedTo100AndAPageBelowOneBecomesOne()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage([], 250);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}?pageSize=1000&page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, host.Stub.LastSearchCall!.Skip);
        Assert.Equal(100, host.Stub.LastSearchCall.Take);
    }

    [Fact]
    public async Task AnEmptyResultIsA200WithAnEmptyPage()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData("minPrice=100&currency=SAR")]
    [InlineData("maxPrice=100&currency=SAR")]
    [InlineData("purpose=Sale&minPrice=100")]
    [InlineData("purpose=Rent&minPrice=100&currency=SAR")]
    [InlineData("purpose=Rent&sort=PriceAsc&currency=SAR")]
    [InlineData("sort=PriceDesc&currency=SAR")]
    [InlineData("purpose=Sale&rentPeriod=Monthly")]
    [InlineData("rentPeriod=Yearly")]
    [InlineData("purpose=Sale&minPrice=200&maxPrice=100&currency=SAR")]
    [InlineData("purpose=Sale&minPrice=-1&currency=SAR")]
    [InlineData("countryCode=XXX")]
    [InlineData("currency=SA")]
    public async Task AnInvalidFilterCombinationIsA422AndNeverReachesTheRepository(string query)
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("validation.failed", root.GetProperty("code").GetString());
        Assert.Equal(422, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        Assert.Equal(0, host.Stub.SearchCallCount);
    }

    [Theory]
    [InlineData("purpose=Auction")]
    [InlineData("propertyType=Castle")]
    [InlineData("rentPeriod=Daily")]
    [InlineData("rentPeriod=Weekly")]
    [InlineData("sort=Cheapest")]
    [InlineData("minPrice=abc")]
    [InlineData("page=abc")]
    [InlineData("pageSize=abc")]
    [InlineData("purpose=99")]
    [InlineData("propertyType=99")]
    [InlineData("rentPeriod=99")]
    [InlineData("sort=99")]
    public async Task AnUnparseableValueIsA400MalformedRequestAndNeverReachesTheRepository(string query)
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request.malformed", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, host.Stub.SearchCallCount);
    }

    [Fact]
    public async Task AFoundListingReturns200WithTheApprovedDetailShape()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Details[ListingId] = Detail(ListingPurpose.Rent, RentPeriod.Monthly);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/{ListingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            new[] { "description", "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root));
        Assert.Equal(ListingId.ToString(), root.GetProperty("listingId").GetString());
        Assert.Equal("Rent", root.GetProperty("purpose").GetString());
        Assert.Equal("Two bedrooms close to the centre.", root.GetProperty("description").GetString());
        Assert.Equal(new[] { "amount", "currency", "rentPeriod" }, SortedNames(root.GetProperty("price")));
        Assert.Equal("Monthly", root.GetProperty("price").GetProperty("rentPeriod").GetString());
    }

    [Fact]
    public async Task ASaleDetailOmitsTheRentPeriodEntirely()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Details[ListingId] = Detail(ListingPurpose.Sale, null);
        using var client = host.CreateClient();

        var body = await client.GetStringAsync($"{ListRoute}/{ListingId}");

        Assert.DoesNotContain("rentPeriod", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnknownOrHiddenListingIsA404WithTheStablePublicCode()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("public_listings.not_found", root.GetProperty("code").GetString());
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task TheEmptyIdIsA404WithoutAskingTheRepository()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetByIdCallCount);
    }

    [Fact]
    public async Task AValueThatIsNotAGuidMatchesNoRoute()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Stub.GetByIdCallCount);
    }

    [Fact]
    public async Task OnlyReadsAreExposed()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var list = await client.SendAsync(new HttpRequestMessage(method, ListRoute));
            using var detail = await client.SendAsync(new HttpRequestMessage(method, $"{ListRoute}/{ListingId}"));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, list.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, detail.StatusCode);
        }
    }

    [Fact]
    public async Task NoResponseBodyContainsAPrivateFieldName()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage([Summary(ListingPurpose.Sale), Summary(ListingPurpose.Rent, RentPeriod.Monthly)], 2);
        host.Stub.Details[ListingId] = Detail(ListingPurpose.Rent, RentPeriod.Monthly);
        using var client = host.CreateClient();

        var list = await client.GetStringAsync(ListRoute);
        var detail = await client.GetStringAsync($"{ListRoute}/{ListingId}");

        foreach (var term in PrivateTerms)
        {
            Assert.DoesNotContain(term, list, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(term, detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EveryResponseCarriesACorrelationId()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        using var client = host.CreateClient();

        using var ok = await client.GetAsync(ListRoute);
        using var notFound = await client.GetAsync($"{ListRoute}/{Guid.NewGuid()}");

        Assert.True(ok.Headers.Contains("X-Correlation-ID"));
        Assert.True(notFound.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task ACorruptRowIsAGeneric500ThatLeaksNothingAndIsNeverSkipped()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Page = new PublicListingPage(
            [Summary(ListingPurpose.Sale), Summary(ListingPurpose.Sale) with { Title = "SENTINEL-TITLE", Location = new PublicListingLocation("SA", "SENTINEL-REGION", " ", "SENTINEL-DISTRICT") }],
            2);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(ListRoute);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("server.unexpected_error", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptDetailIsAGeneric500ThatLeaksNothing()
    {
        using var host = new PublicApiTestHost(new StubPublicListingReadRepository());
        host.Stub.Details[ListingId] = Detail(ListingPurpose.Rent, null) with { Title = "SENTINEL-TITLE" };
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/{ListingId}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("server.unexpected_error", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProductionContainerResolvesBothHandlersAndTheRealRepositoryWithoutAnyTestOverride()
    {
        using var host = new PublicApiTestHost(stub: null);
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var search = provider.GetRequiredService<IQueryHandler<SearchPublicListingsQuery, PagedList<PublicListingSummary>>>();
        var detail = provider.GetRequiredService<IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail>>();
        var repository = provider.GetRequiredService<IPublicListingReadRepository>();

        Assert.IsType<SearchPublicListingsQueryHandler>(search);
        Assert.IsType<GetPublicListingByIdQueryHandler>(detail);
        Assert.Equal("SmartProperty.Persistence", repository.GetType().Assembly.GetName().Name);
        Assert.Equal("PublicListingReadRepository", repository.GetType().Name);
        Assert.NotEqual(typeof(StubPublicListingReadRepository), repository.GetType());
    }

    private static string[] SortedNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static PublicListingSummary Summary(ListingPurpose purpose, RentPeriod? rentPeriod = null)
    {
        return new PublicListingSummary(
            ListingId,
            purpose,
            purpose == ListingPurpose.Rent ? "Bright apartment" : "Corner villa",
            purpose == ListingPurpose.Rent
                ? new PublicListingPrice(5200m, "SAR", rentPeriod)
                : new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);
    }

    private static PublicListingDetail Detail(ListingPurpose purpose, RentPeriod? rentPeriod)
    {
        return new PublicListingDetail(
            ListingId,
            purpose,
            purpose == ListingPurpose.Rent ? "Bright apartment" : "Corner villa",
            purpose == ListingPurpose.Rent ? "Two bedrooms close to the centre." : "Quiet street, 450 square metres.",
            purpose == ListingPurpose.Rent
                ? new PublicListingPrice(5200m, "SAR", rentPeriod)
                : new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);
    }
}

/// <summary>
/// A real API host for the public marketplace tests. With a stub it replaces the read repository for that host only;
/// with <see langword="null"/> it is the untouched production container.
/// </summary>
/// <remarks>
/// The connection string points nowhere and is never used: nothing in these tests opens a database connection.
/// </remarks>
internal sealed class PublicApiTestHost : IDisposable
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private readonly SmartPropertyApiFactory _root;
    private readonly WebApplicationFactory<Program> _host;

    public PublicApiTestHost(StubPublicListingReadRepository? stub)
    {
        Stub = stub!;
        _root = new SmartPropertyApiFactory(UnusedConnectionString);
        _host = _root.WithWebHostBuilder(builder =>
        {
            // AddPersistence reads the connection string while Program is being composed, before the factory's late
            // configuration callback runs. A host setting is visible at that point and is local to this host, unlike
            // a process-wide environment variable. Nothing opens a connection with it.
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);

            if (stub is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPublicListingReadRepository>();
                    services.AddSingleton<IPublicListingReadRepository>(stub);
                });
            }
        });
    }

    public StubPublicListingReadRepository Stub { get; }

    public IServiceProvider Services => _host.Services;

    public HttpClient CreateClient() => _host.CreateClient();

    public void Dispose()
    {
        if (!ReferenceEquals(_host, _root))
        {
            _host.Dispose();
        }

        _root.Dispose();
    }
}
