using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyReadEndpointTests(ApiPostgreSqlFixture fixture)
{
    private const string Route = "/api/properties";
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    // The registry is not public: visitors see properties through published listings only.
    [Theory]
    [InlineData("/api/properties", HttpStatusCode.Unauthorized)]
    [InlineData("/api/properties/11111111-1111-1111-1111-111111111111", HttpStatusCode.Unauthorized)]
    public async Task AnonymousReadIsRefused(string route, HttpStatusCode expected)
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(route);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/properties", HttpStatusCode.OK)]
    [InlineData("/api/properties/11111111-1111-1111-1111-111111111111", HttpStatusCode.NotFound)]
    public async Task ASignedInCallerWithoutPermissionReadsOnlyTheirOwn(string route, HttpStatusCode expected)
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync(route);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedListReturnsFilteredPageNewestFirstWithEveryFieldMapped()
    {
        var older = NewProperty(PropertyType.Apartment, "XZ", BaseTime, city: "Older City");
        var newer = NewProperty(
            PropertyType.Apartment,
            "XZ",
            BaseTime.AddMinutes(1),
            city: "Newer City",
            region: "Region",
            district: "District",
            addressLine: "Address line",
            postalCode: "12345",
            latitude: 24.7136m,
            longitude: 46.6753m);
        var wrongType = NewProperty(PropertyType.Land, "XZ", BaseTime.AddMinutes(2));
        var wrongCountry = NewProperty(PropertyType.Apartment, "XY", BaseTime.AddMinutes(3));
        await SeedAsync(older, newer, wrongType, wrongCountry);
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        var body = await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>(
            $"{Route}?type=Apartment&status=Active&countryCode=%20xz%20&page=1&pageSize=1");

        Assert.NotNull(body);
        Assert.Equal(1, body.Page);
        Assert.Equal(1, body.PageSize);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(2, body.TotalPages);
        Assert.True(body.HasNext);
        Assert.False(body.HasPrevious);
        var property = Assert.Single(body.Items);
        Assert.Equal(newer.Id, property.Id);
        Assert.Equal("Apartment", property.Type);
        Assert.Equal("Active", property.Status);
        Assert.Equal("XZ", property.Address.CountryCode);
        Assert.Equal("Newer City", property.Address.City);
        Assert.Equal("Region", property.Address.Region);
        Assert.Equal("District", property.Address.District);
        Assert.Equal("Address line", property.Address.AddressLine);
        Assert.Equal("12345", property.Address.PostalCode);
        Assert.Equal(24.7136m, property.Address.Latitude);
        Assert.Equal(46.6753m, property.Address.Longitude);
        Assert.Equal(newer.CreatedAt, property.CreatedAt);
        Assert.Equal(newer.UpdatedAt, property.UpdatedAt);
    }

    [Fact]
    public async Task ListWithoutStatusReturnsEveryStatusAndStatusArchivedReturnsArchivedOnes()
    {
        var active = NewProperty(PropertyType.Building, "XW", BaseTime);
        var archived = NewProperty(PropertyType.Building, "XW", BaseTime.AddHours(1));
        archived.Archive(BaseTime.AddDays(1));
        await SeedAsync(active, archived);
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        var byDefault = await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>($"{Route}?countryCode=XW");
        var archivedOnly = await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>(
            $"{Route}?countryCode=XW&status=Archived");

        Assert.Equal(new[] { archived.Id, active.Id }, byDefault!.Items.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { "Archived", "Active" }, byDefault.Items.Select(item => item.Status).ToArray());
        var archivedItem = Assert.Single(archivedOnly!.Items);
        Assert.Equal(archived.Id, archivedItem.Id);
        Assert.Equal("Archived", archivedItem.Status);
    }

    [Fact]
    public async Task SearchAndSortParametersAreAcceptedAndBadValuesAreRefused()
    {
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var ok = await client.GetAsync($"{Route}?countryCode=XU&q=villa%20hittin&sort=Newest");
        using var badSort = await client.GetAsync($"{Route}?sort=Cheapest");
        // Price is a listing fact, so a property cannot be sorted by it any more.
        using var priceSort = await client.GetAsync($"{Route}?sort=PriceDesc");
        using var tooManyWords = await client.GetAsync($"{Route}?q=a%20b%20c%20d%20e%20f");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, priceSort.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooManyWords.StatusCode);
    }

    [Fact]
    public async Task ShareReturnsThePathAndRequestOriginUrlOfAnActivePropertyAnonymously()
    {
        var property = NewProperty(PropertyType.Villa, "XT", BaseTime);
        await SeedAsync(property);
        KeyValuePair<string, string?>[] noBaseUrl = [.. BootstrapDisabled, new("Share:PublicBaseUrl", "")];
        using var factory = new SmartPropertyApiFactory(fixture, configuration: noBaseUrl);
        using var host = Advertising(factory, property.Id);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{Route}/{property.Id}/share");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>());
        Assert.Equal(property.Id, body.GetProperty("propertyId").GetGuid());
        Assert.Equal($"/properties/{property.Id}", body.GetProperty("path").GetString());
        Assert.Equal($"http://localhost/properties/{property.Id}", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ShareBuildsAnAbsoluteUrlWhenThePublicBaseUrlIsConfigured()
    {
        var property = NewProperty(PropertyType.Villa, "XS", BaseTime);
        await SeedAsync(property);
        KeyValuePair<string, string?>[] configuration =
        [
            .. BootstrapDisabled,
            new("Share:PublicBaseUrl", "https://app.example.com/"),
            new("Share:PropertyPath", "/listing/{id}")
        ];
        using var factory = new SmartPropertyApiFactory(fixture, configuration: configuration);
        using var host = Advertising(factory, property.Id);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{Route}/{property.Id}/share");

        Assert.Equal($"https://app.example.com/listing/{property.Id}", body.GetProperty("url").GetString());
        Assert.Equal($"/listing/{property.Id}", body.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ShareOfAnArchivedUnknownOrUnadvertisedPropertyIs404()
    {
        var archived = NewProperty(PropertyType.Villa, "XR", BaseTime);
        archived.Archive(BaseTime.AddDays(1));
        var unadvertised = NewProperty(PropertyType.Villa, "XR", BaseTime);
        await SeedAsync(archived, unadvertised);
        using var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        using var host = Advertising(factory, archived.Id);
        using var client = host.CreateClient();

        using var archivedResponse = await client.GetAsync($"{Route}/{archived.Id}/share");
        using var unknownResponse = await client.GetAsync($"{Route}/{Guid.NewGuid()}/share");
        using var unadvertisedResponse = await client.GetAsync($"{Route}/{unadvertised.Id}/share");

        Assert.Equal(HttpStatusCode.NotFound, archivedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unadvertisedResponse.StatusCode);
    }

    [Fact]
    public async Task GetByIdReturnsAnArchivedProperty()
    {
        var archived = NewProperty(PropertyType.Land, "XV", BaseTime);
        archived.Archive(BaseTime.AddHours(1));
        await SeedAsync(archived);
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{Route}/{archived.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(archived.Id, body.Id);
        Assert.Equal("Land", body.Type);
        Assert.Equal("Archived", body.Status);
        Assert.Equal("XV", body.Address.CountryCode);
        Assert.Equal(BaseTime, body.CreatedAt);
        Assert.Equal(BaseTime.AddHours(1), body.UpdatedAt);
    }

    [Fact]
    public async Task UnknownPropertyReturns404WithTheStandardError()
    {
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.not_found", body.Code);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("XYZ")]
    [InlineData("X1")]
    public async Task InvalidCountryCodeReturns422(string countryCode)
    {
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{Route}?countryCode={countryCode}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", body.Code);
    }

    [Fact]
    public async Task UnparseableEnumReturns400()
    {
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{Route}?type=House");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("request.malformed", body.Code);
    }

    // An administrator: property.update is what lets a reader see every property.
    private SmartPropertyApiFactory ReadAuthorizedFactory()
    {
        var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return factory;
    }

    // A host in which only the given properties have a published listing, so they alone can be shared.
    private static WebApplicationFactory<Program> Advertising(SmartPropertyApiFactory factory, params Guid[] advertised) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IPropertyListingsLookup>(_ => new AdvertisedListings(advertised))));

    private sealed class AdvertisedListings(Guid[] advertised) : IPropertyListingsLookup
    {
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>>> GetCurrentAsync(
            IReadOnlyCollection<Guid> propertyIds, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>> result = propertyIds
                .Where(advertised.Contains)
                .ToDictionary(id => id, _ => (IReadOnlyList<PublicListingSummary>)
                [
                    new PublicListingSummary(Guid.NewGuid(), ListingPurpose.Sale, "Listed", new PublicListingPrice(1m, "EGP", null),
                        PropertyType.Villa, new PublicListingLocation("EG", "Cairo", "Cairo", "Maadi"), DateTimeOffset.UtcNow),
                ]);

            return Task.FromResult(result);
        }

        public Task<IReadOnlyDictionary<Guid, PropertyListingReference>> GetByListingIdsAsync(
            IReadOnlyCollection<Guid> listingIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PropertyListingReference>>(new Dictionary<Guid, PropertyListingReference>());
    }

    private async Task SeedAsync(params Property[] properties)
    {
        await using var context = fixture.CreateContext();
        context.Properties.AddRange(properties);
        await context.SaveChangesAsync();
    }

    private static Property NewProperty(
        PropertyType type,
        string countryCode,
        DateTimeOffset createdAt,
        string? city = null,
        string? region = null,
        string? district = null,
        string? addressLine = null,
        string? postalCode = null,
        decimal? latitude = null,
        decimal? longitude = null)
    {
        return new Property(
            Guid.NewGuid(),
            type,
            new PropertyAddress(
                countryCode,
                city,
                region,
                district,
                addressLine,
                postalCode,
                latitude,
                longitude),
            createdAt);
    }
}
