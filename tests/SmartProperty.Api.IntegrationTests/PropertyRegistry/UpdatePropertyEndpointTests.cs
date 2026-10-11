using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class UpdatePropertyEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public UpdatePropertyEndpointTests(ApiPostgreSqlFixture fixture)
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
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"{Route}/{property.Id}", MinimumRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.unauthorized", error.Code);
    }

    [Fact]
    public async Task AuthenticatedCallerWithoutPermissionReturns403AndChangesNothing()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Unit", "AE"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authorization.forbidden", error.Code);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(property.UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task FullRequestReturns200NormalizesAndPersistsEveryMutableField()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        var request = new UpdatePropertyRequest(
            "Building",
            " ae ",
            " Dubai ",
            " Dubai Emirate ",
            " Downtown ",
            " Sheikh Zayed Road ",
            " 00000 ",
            25.1972m,
            55.2744m);

        using var response = await client.PutAsJsonAsync($"{Route}/{property.Id}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(property.Id, body.Id);
        Assert.Equal("Building", body.Type);
        Assert.Equal("Active", body.Status);
        Assert.Equal("AE", body.Address.CountryCode);
        Assert.Equal("Dubai", body.Address.City);
        Assert.Equal("Dubai Emirate", body.Address.Region);
        Assert.Equal("Downtown", body.Address.District);
        Assert.Equal("Sheikh Zayed Road", body.Address.AddressLine);
        Assert.Equal("00000", body.Address.PostalCode);
        Assert.Equal(25.1972m, body.Address.Latitude);
        Assert.Equal(55.2744m, body.Address.Longitude);
        Assert.Equal(property.CreatedAt, body.CreatedAt);
        Assert.True(body.UpdatedAt > property.UpdatedAt);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyType.Building, persisted.Type);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal("AE", persisted.Address.CountryCode);
        Assert.Equal("Dubai", persisted.Address.City);
        Assert.Equal("Dubai Emirate", persisted.Address.Region);
        Assert.Equal("Downtown", persisted.Address.District);
        Assert.Equal("Sheikh Zayed Road", persisted.Address.AddressLine);
        Assert.Equal("00000", persisted.Address.PostalCode);
        Assert.Equal(25.1972m, persisted.Address.Latitude);
        Assert.Equal(55.2744m, persisted.Address.Longitude);
        Assert.Equal(property.CreatedAt, persisted.CreatedAt);
        Assert.Equal(body.UpdatedAt, persisted.UpdatedAt);

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Properties.CountAsync(row => row.Id == property.Id));
    }

    [Fact]
    public async Task ResponseHasExactlyThePropertyResponseShape()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync($"{Route}/{property.Id}", new UpdatePropertyRequest("Unit", "SA"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["address", "createdAt", "id", "status", "type", "updatedAt"],
            PropertyNames(document.RootElement));
        Assert.Equal(
            ["addressLine", "city", "countryCode", "district", "latitude", "longitude", "postalCode", "region"],
            PropertyNames(document.RootElement.GetProperty("address")));
    }

    [Fact]
    public async Task OmittedOptionalFieldsAreCleared()
    {
        var property = await SeedPropertyAsync(new PropertyAddress(
            "SA",
            "Riyadh",
            "Riyadh Province",
            "Al Olaya",
            "King Fahd Road",
            "12211",
            24.7136m,
            46.6753m));
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new { type = "Land", countryCode = "SA" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Null(body.Address.City);
        Assert.Null(body.Address.Region);
        Assert.Null(body.Address.District);
        Assert.Null(body.Address.AddressLine);
        Assert.Null(body.Address.PostalCode);
        Assert.Null(body.Address.Latitude);
        Assert.Null(body.Address.Longitude);

        var persisted = await LoadAsync(property.Id);
        Assert.Null(persisted.Address.City);
        Assert.Null(persisted.Address.Region);
        Assert.Null(persisted.Address.District);
        Assert.Null(persisted.Address.AddressLine);
        Assert.Null(persisted.Address.PostalCode);
        Assert.Null(persisted.Address.Latitude);
        Assert.Null(persisted.Address.Longitude);
    }

    [Fact]
    public async Task WhitespaceOptionalValuesNormalizeToNull()
    {
        var property = await SeedPropertyAsync(new PropertyAddress("SA", "Riyadh", "Riyadh Province"));
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Land", "SA", City: "   ", Region: "", District: " Al Olaya "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Null(body.Address.City);
        Assert.Null(body.Address.Region);
        Assert.Equal("Al Olaya", body.Address.District);
    }

    [Theory]
    [InlineData("24.7136", null)]
    [InlineData(null, "46.6753")]
    [InlineData("-90", "180")]
    public async Task EitherCoordinateCanBeSuppliedIndependently(string? latitude, string? longitude)
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Land", "SA", Latitude: Parse(latitude), Longitude: Parse(longitude)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(Parse(latitude), body.Address.Latitude);
        Assert.Equal(Parse(longitude), body.Address.Longitude);
    }

    [Theory]
    [InlineData(null, "SA")]
    [InlineData("", "SA")]
    [InlineData("House", "SA")]
    [InlineData("land", "SA")]
    [InlineData("1", "SA")]
    [InlineData("Land", null)]
    [InlineData("Land", "")]
    [InlineData("Land", "S")]
    [InlineData("Land", "SAU")]
    [InlineData("Land", "S1")]
    public async Task InvalidTypeOrCountryCodeReturns422AndChangesNothing(string? type, string? countryCode)
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest(type, countryCode));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(property.UpdatedAt, (await LoadAsync(property.Id)).UpdatedAt);
    }

    [Theory]
    [InlineData("-90.01", null)]
    [InlineData("90.01", null)]
    [InlineData(null, "-180.01")]
    [InlineData(null, "180.01")]
    public async Task InvalidCoordinateReturns422(string? latitude, string? longitude)
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Land", "SA", Latitude: Parse(latitude), Longitude: Parse(longitude)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
    }

    [Fact]
    public async Task UnknownIdReturns404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync($"{Route}/{Guid.NewGuid()}", MinimumRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.not_found", error.Code);
    }

    [Fact]
    public async Task NonGuidIdIsNotRoutedAndReturns404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync($"{Route}/not-a-guid", MinimumRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"type\":\"Land\",\"countryCode\":\"SA\",\"latitude\":\"north\"}")]
    public async Task MalformedBodyReturns400(string json)
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PutAsync($"{Route}/{property.Id}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("request.malformed", error.Code);
    }

    [Fact]
    public async Task ArchivedPropertyReturns409AndIsNotChanged()
    {
        var property = await SeedPropertyAsync(archive: true);
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Unit", "AE", "Dubai"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.archived", error.Code);
        Assert.Equal(409, error.Status);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal("SA", persisted.Address.CountryCode);
        Assert.Null(persisted.Address.City);
        Assert.Equal(property.UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task ServerControlledFieldsCannotBeOverridden()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();
        var requestedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new
        {
            type = "Unit",
            countryCode = "SA",
            id = Guid.NewGuid(),
            status = "Archived",
            createdAt = requestedTime,
            updatedAt = requestedTime
        };

        using var response = await client.PutAsJsonAsync($"{Route}/{property.Id}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(property.Id, body.Id);
        Assert.Equal("Active", body.Status);
        Assert.Equal(property.CreatedAt, body.CreatedAt);
        Assert.NotEqual(requestedTime, body.UpdatedAt);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal(property.CreatedAt, persisted.CreatedAt);
        Assert.NotEqual(requestedTime, persisted.UpdatedAt);
    }

    [Fact]
    public async Task PersistedStateIsVisibleOnAFollowUpGet()
    {
        var property = await SeedPropertyAsync();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        using var client = AuthorizedClient();

        using var update = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Building", "EG", "Cairo", Latitude: 30.0444m));
        var updated = (await update.Content.ReadFromJsonAsync<PropertyResponse>())!;

        var fetched = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{property.Id}"))!;

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Building", fetched.Type);
        Assert.Equal("EG", fetched.Address.CountryCode);
        Assert.Equal("Cairo", fetched.Address.City);
        Assert.Equal(30.0444m, fetched.Address.Latitude);
        Assert.Equal(updated.Address, fetched.Address);
        Assert.Equal(updated.UpdatedAt, fetched.UpdatedAt);
        Assert.Equal(updated.UpdatedAt, (await LoadAsync(property.Id)).UpdatedAt);
    }

    [Fact]
    public async Task RealChangeWithTheServerClockBehindKeepsTheCurrentUpdateInstantExactly()
    {
        // Seeded an hour ahead of the server clock, so the server's UtcNow is behind the stored update instant.
        var property = await SeedPropertyAsync(createdAtOffset: TimeSpan.FromHours(1));
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        using var client = AuthorizedClient();

        using var update = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Unit", "AE", "Dubai"));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Unit", updated.Type);
        Assert.Equal("Dubai", updated.Address.City);
        Assert.Equal(property.UpdatedAt, updated.UpdatedAt);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyType.Unit, persisted.Type);
        Assert.Equal(updated.UpdatedAt, persisted.UpdatedAt);

        var fetched = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{property.Id}"))!;
        Assert.Equal(updated.UpdatedAt, fetched.UpdatedAt);
    }

    [Fact]
    public async Task NoOpUpdateReturns200AndKeepsUpdatedAt()
    {
        var property = await SeedPropertyAsync(new PropertyAddress("SA", "Riyadh", latitude: 24.7m));
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Land", " sa ", " Riyadh ", PostalCode: "  ", Latitude: 24.70m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(property.UpdatedAt, body.UpdatedAt);
        Assert.Equal("Riyadh", body.Address.City);
        Assert.Equal(property.UpdatedAt, (await LoadAsync(property.Id)).UpdatedAt);
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    private static UpdatePropertyRequest MinimumRequest() => new("Land", "SA");

    /// <summary>
    /// A seeded property whose timestamps are aligned to PostgreSQL microsecond precision, so they round-trip
    /// exactly. By default it is created ten minutes in the past; <paramref name="createdAtOffset"/> can instead
    /// place the stored timestamp ahead of the server clock, for clock-skew regression tests.
    /// </summary>
    private async Task<Property> SeedPropertyAsync(
        PropertyAddress? address = null,
        bool archive = false,
        TimeSpan? createdAtOffset = null)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero)
            .Add(createdAtOffset ?? TimeSpan.FromMinutes(-10));
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address ?? new PropertyAddress("SA"), createdAt);

        if (archive)
        {
            property.Archive(createdAt.AddMinutes(1));
        }

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }

    private async Task<Property> LoadAsync(Guid id)
    {
        await using var context = _fixture.CreateContext();
        return await context.Properties.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private static string[] PropertyNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static decimal? Parse(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
