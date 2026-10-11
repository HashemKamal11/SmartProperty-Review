using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
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

    [Theory]
    [InlineData("/api/properties")]
    [InlineData("/api/properties/11111111-1111-1111-1111-111111111111")]
    public async Task UnauthenticatedReadReturns401(string route)
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/properties")]
    [InlineData("/api/properties/11111111-1111-1111-1111-111111111111")]
    public async Task AuthenticatedCallerWithoutPermissionReturns403(string route)
    {
        using var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedListReturnsFilteredPageNewestFirstWithEveryFieldMapped()
    {
        var older = NewProperty(PropertyType.Unit, "XZ", BaseTime, city: "Older City");
        var newer = NewProperty(
            PropertyType.Unit,
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
        var wrongCountry = NewProperty(PropertyType.Unit, "XY", BaseTime.AddMinutes(3));
        await SeedAsync(older, newer, wrongType, wrongCountry);
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        var body = await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>(
            $"{Route}?type=Unit&status=Active&countryCode=%20xz%20&page=1&pageSize=1");

        Assert.NotNull(body);
        Assert.Equal(1, body.Page);
        Assert.Equal(1, body.PageSize);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(2, body.TotalPages);
        Assert.True(body.HasNext);
        Assert.False(body.HasPrevious);
        var property = Assert.Single(body.Items);
        Assert.Equal(newer.Id, property.Id);
        Assert.Equal("Unit", property.Type);
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
    public async Task ListWithoutStatusIncludesArchivedProperties()
    {
        var archived = NewProperty(PropertyType.Building, "XW", BaseTime);
        archived.Archive(BaseTime.AddDays(1));
        await SeedAsync(archived);
        using var factory = ReadAuthorizedFactory();
        using var client = factory.CreateAuthenticatedClient(Guid.NewGuid());

        var body = await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>(
            $"{Route}?countryCode=XW");

        var property = Assert.Single(body!.Items);
        Assert.Equal(archived.Id, property.Id);
        Assert.Equal("Archived", property.Status);
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

    private SmartPropertyApiFactory ReadAuthorizedFactory()
    {
        var factory = new SmartPropertyApiFactory(fixture, configuration: BootstrapDisabled);
        factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        return factory;
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
