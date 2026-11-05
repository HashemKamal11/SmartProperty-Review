using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers the amenity list endpoint and choosing amenities when a property is created or updated, against the
/// real host and a migrated database, so the seeded amenities are the real ones.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyAmenitiesEndpointTests : IDisposable
{
    private const string PropertiesRoute = "/api/properties";
    private const string AmenitiesRoute = "/api/amenities";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertyAmenitiesEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task ListingAmenitiesWithoutSigningInReturns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(AmenitiesRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnySignedInUserCanListTheFortyOneSeededAmenities()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var amenities = (await client.GetFromJsonAsync<AmenityResponse[]>(AmenitiesRoute))!;

        Assert.Equal(41, amenities.Length);
        Assert.Equal("Balcony", amenities[0].Code);
        Assert.Equal("شرفة", amenities[0].NameAr);
        Assert.Equal("Balcony", amenities[0].NameEn);
        Assert.Equal("Kitchen", amenities[^1].Code);
        Assert.Equal(41, amenities.Select(a => a.Code).Distinct().Count());
    }

    [Fact]
    public async Task CreateWithAmenityCodesReturnsThemInDisplayOrderAndPersistsThem()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            PropertiesRoute,
            new CreatePropertyRequest("Apartment", "EG", AmenityCodes: ["SwimmingPool", "Balcony", "PrivateGym"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal(["Balcony", "SwimmingPool", "PrivateGym"], body.Amenities!.Select(a => a.Code));
        Assert.Equal("شرفة", body.Amenities![0].NameAr);

        Assert.Equal(["Balcony", "SwimmingPool", "PrivateGym"], await LoadCodesAsync(body.Id));
    }

    [Fact]
    public async Task CreateWithoutAmenityCodesReturnsAnEmptyList()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(PropertiesRoute, new CreatePropertyRequest("Land", "EG"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.NotNull(body.Amenities);
        Assert.Empty(body.Amenities);
    }

    [Fact]
    public async Task CreateWithDuplicateCodesStoresEachAmenityOnce()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            PropertiesRoute,
            new CreatePropertyRequest("Apartment", "EG", AmenityCodes: ["Balcony", "Balcony", " Balcony "]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal(["Balcony"], body.Amenities!.Select(a => a.Code));
    }

    [Fact]
    public async Task CreateWithAnUnknownCodeReturns422NamingItAndStoresNothing()
    {
        using var client = AuthorizedClient();
        var countBefore = await CountPropertiesAsync();

        using var response = await client.PostAsJsonAsync(
            PropertiesRoute,
            new CreatePropertyRequest("Apartment", "EG", AmenityCodes: ["Balcony", "Helipad"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Contains("Helipad", error.Message);
        Assert.Equal(countBefore, await CountPropertiesAsync());
    }

    [Fact]
    public async Task CreateWithABlankCodeReturns422()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            PropertiesRoute,
            new CreatePropertyRequest("Apartment", "EG", AmenityCodes: ["Balcony", "  "]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReplacesTheAmenitiesAndOmittingThemClearsThem()
    {
        var property = await SeedAsync("SwimmingPool", "Balcony");
        using var client = AuthorizedClient();

        using var replaced = await client.PutAsJsonAsync(
            $"{PropertiesRoute}/{property.Id}",
            new UpdatePropertyRequest("Apartment", "EG", AmenityCodes: ["Balcony", "Security"]));

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var body = (await replaced.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(["Balcony", "Security"], body.Amenities!.Select(a => a.Code));
        Assert.Equal(["Balcony", "Security"], await LoadCodesAsync(property.Id));

        using var cleared = await client.PutAsJsonAsync(
            $"{PropertiesRoute}/{property.Id}",
            new UpdatePropertyRequest("Apartment", "EG"));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty(await LoadCodesAsync(property.Id));
    }

    [Fact]
    public async Task UpdateWithAnUnknownCodeReturns422AndKeepsTheCurrentAmenities()
    {
        var property = await SeedAsync("Balcony");
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{PropertiesRoute}/{property.Id}",
            new UpdatePropertyRequest("Apartment", "EG", AmenityCodes: ["Helipad"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["Balcony"], await LoadCodesAsync(property.Id));
    }

    [Fact]
    public async Task GetByIdAndListReturnEachPropertysAmenities()
    {
        var property = await SeedAsync("SharedGym", "Security");
        using var client = AuthorizedClient();

        var single = (await client.GetFromJsonAsync<PropertyResponse>($"{PropertiesRoute}/{property.Id}"))!;
        Assert.Equal(["Security", "SharedGym"], single.Amenities!.Select(a => a.Code));

        var page = (await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>($"{PropertiesRoute}?pageSize=100"))!;
        var listed = Assert.Single(page.Items, item => item.Id == property.Id);
        Assert.Equal(["Security", "SharedGym"], listed.Amenities!.Select(a => a.Code));
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(TestUsers.Seed(_fixture));
    }

    private async Task<Property> SeedAsync(params string[] amenityCodes)
    {
        await using var context = _fixture.CreateContext();
        var amenities = await context.Amenities.Where(a => amenityCodes.Contains(a.Code)).ToListAsync();
        var now = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero).AddMinutes(-10);
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), createdAt, amenities: amenities);

        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }

    private async Task<string[]> LoadCodesAsync(Guid propertyId)
    {
        await using var context = _fixture.CreateContext();
        var property = await context.Properties
            .Include(p => p.Amenities)
            .AsNoTracking()
            .SingleAsync(p => p.Id == propertyId);

        return property.Amenities.OrderBy(a => a.SortOrder).Select(a => a.Code).ToArray();
    }

    private async Task<int> CountPropertiesAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.Properties.CountAsync();
    }
}
