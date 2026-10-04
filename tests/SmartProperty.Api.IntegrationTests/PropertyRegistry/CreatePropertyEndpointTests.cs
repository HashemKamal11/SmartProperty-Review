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

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class CreatePropertyEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public CreatePropertyEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task UnauthenticatedRequestReturns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Route, MinimumRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedCallerWithoutPermissionReturns403()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.PostAsJsonAsync(Route, MinimumRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedMinimumRequestReturns201AndPersistsCanonicalProperty()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, MinimumRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal("Land", body.Type);
        Assert.Equal("Active", body.Status);
        Assert.Equal("SA", body.Address.CountryCode);
        Assert.Null(body.Address.City);
        Assert.Null(body.Address.Region);
        Assert.Null(body.Address.District);
        Assert.Null(body.Address.AddressLine);
        Assert.Null(body.Address.PostalCode);
        Assert.Null(body.Address.Latitude);
        Assert.Null(body.Address.Longitude);
        Assert.Equal(body.CreatedAt, body.UpdatedAt);

        await using var context = _fixture.CreateContext();
        var persisted = await context.Properties.AsNoTracking().SingleAsync(row => row.Id == body.Id);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal("SA", persisted.Address.CountryCode);
        Assert.Equal(persisted.CreatedAt, persisted.UpdatedAt);
        Assert.InRange(
            (body.CreatedAt - persisted.CreatedAt).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromTicks(9));
    }

    [Fact]
    public async Task AuthorizedFullRequestNormalizesAndPersistsEveryAddressField()
    {
        using var client = AuthorizedClient();
        var request = new CreatePropertyRequest(
            "Building",
            " sa ",
            " Riyadh ",
            " Riyadh Province ",
            " Al Olaya ",
            " King Fahd Road ",
            " 12211 ",
            24.7136m,
            46.6753m);

        using var response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal("Building", body.Type);
        Assert.Equal("SA", body.Address.CountryCode);
        Assert.Equal("Riyadh", body.Address.City);
        Assert.Equal("Riyadh Province", body.Address.Region);
        Assert.Equal("Al Olaya", body.Address.District);
        Assert.Equal("King Fahd Road", body.Address.AddressLine);
        Assert.Equal("12211", body.Address.PostalCode);
        Assert.Equal(24.7136m, body.Address.Latitude);
        Assert.Equal(46.6753m, body.Address.Longitude);

        await using var context = _fixture.CreateContext();
        var persisted = await context.Properties.AsNoTracking().SingleAsync(row => row.Id == body.Id);
        Assert.Equal(body.Address.CountryCode, persisted.Address.CountryCode);
        Assert.Equal(body.Address.City, persisted.Address.City);
        Assert.Equal(body.Address.Region, persisted.Address.Region);
        Assert.Equal(body.Address.District, persisted.Address.District);
        Assert.Equal(body.Address.AddressLine, persisted.Address.AddressLine);
        Assert.Equal(body.Address.PostalCode, persisted.Address.PostalCode);
        Assert.Equal(body.Address.Latitude, persisted.Address.Latitude);
        Assert.Equal(body.Address.Longitude, persisted.Address.Longitude);
    }

    [Theory]
    [InlineData(null, "SA")]
    [InlineData("", "SA")]
    [InlineData("House", "SA")]
    [InlineData("1", "SA")]
    [InlineData("Land", null)]
    [InlineData("Land", "")]
    [InlineData("Land", "S")]
    [InlineData("Land", "SAU")]
    [InlineData("Land", "S1")]
    public async Task InvalidTypeOrCountryCodeReturns422(string? type, string? countryCode)
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, new CreatePropertyRequest(type, countryCode));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
    }

    [Theory]
    [InlineData("-90.01", null)]
    [InlineData("90.01", null)]
    [InlineData(null, "-180.01")]
    [InlineData(null, "180.01")]
    public async Task InvalidCoordinateReturns422(string? latitude, string? longitude)
    {
        using var client = AuthorizedClient();
        var request = new CreatePropertyRequest(
            "Land",
            "SA",
            Latitude: Parse(latitude),
            Longitude: Parse(longitude));

        using var response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("24.7136", null)]
    [InlineData(null, "46.6753")]
    public async Task EitherCoordinateCanBeSuppliedIndependently(string? latitude, string? longitude)
    {
        using var client = AuthorizedClient();
        var request = new CreatePropertyRequest(
            "Unit",
            "SA",
            Latitude: Parse(latitude),
            Longitude: Parse(longitude));

        using var response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal(Parse(latitude), body.Address.Latitude);
        Assert.Equal(Parse(longitude), body.Address.Longitude);
    }

    [Fact]
    public async Task ServerControlledFieldsCannotBeOverridden()
    {
        using var client = AuthorizedClient();
        var requestedId = Guid.NewGuid();
        var requestedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new
        {
            type = "Land",
            countryCode = "SA",
            id = requestedId,
            status = "Archived",
            createdAt = requestedTime,
            updatedAt = requestedTime
        };

        using var response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.NotEqual(requestedId, body.Id);
        Assert.Equal("Active", body.Status);
        Assert.NotEqual(requestedTime, body.CreatedAt);
        Assert.Equal(body.CreatedAt, body.UpdatedAt);
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        return _factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    private static CreatePropertyRequest MinimumRequest() => new("Land", "SA");

    private static decimal? Parse(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
