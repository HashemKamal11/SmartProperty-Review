using System.Net;
using System.Net.Http.Json;
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
public sealed class ArchivePropertyEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public ArchivePropertyEndpointTests(ApiPostgreSqlFixture fixture)
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

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authentication.unauthorized", error.Code);
    }

    [Fact]
    public async Task AuthenticatedCallerWithoutPermissionReturns403AndChangesNothing()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("authorization.forbidden", error.Code);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal(property.UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task ActivePropertyReturns200ArchivedAndPersistsOnlyStatusAndUpdatedAt()
    {
        var property = await SeedPropertyAsync(new PropertyAddress("SA", "Riyadh", latitude: 24.7136m));
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(property.Id, body.Id);
        Assert.Equal("Land", body.Type);
        Assert.Equal("Archived", body.Status);
        Assert.Equal("SA", body.Address.CountryCode);
        Assert.Equal("Riyadh", body.Address.City);
        Assert.Equal(24.7136m, body.Address.Latitude);
        Assert.Equal(property.CreatedAt, body.CreatedAt);
        Assert.True(body.UpdatedAt > property.UpdatedAt);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(body.UpdatedAt, persisted.UpdatedAt);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(property.Address, persisted.Address);
        Assert.Equal(property.CreatedAt, persisted.CreatedAt);

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Properties.CountAsync(row => row.Id == property.Id));
    }

    [Fact]
    public async Task ResponseHasExactlyThePropertyResponseShape()
    {
        var property = await SeedPropertyAsync();
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

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
    public async Task PersistedStateIsVisibleOnAFollowUpGet()
    {
        var property = await SeedPropertyAsync();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        using var client = AuthorizedClient();

        using var archive = await client.PostAsync(ArchiveRoute(property.Id), content: null);
        var archived = (await archive.Content.ReadFromJsonAsync<PropertyResponse>())!;

        var fetched = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{property.Id}"))!;

        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal("Archived", fetched.Status);
        Assert.Equal(archived.UpdatedAt, fetched.UpdatedAt);
        Assert.Equal(archived.Address, fetched.Address);
    }

    [Fact]
    public async Task ArchivedPropertyIsStillListedAndReadableById()
    {
        var property = await SeedPropertyAsync(new PropertyAddress("QZ"));
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        using var client = AuthorizedClient();

        using var archive = await client.PostAsync(ArchiveRoute(property.Id), content: null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var list = (await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>($"{Route}?countryCode=QZ"))!;
        var listed = Assert.Single(list.Items, item => item.Id == property.Id);
        Assert.Equal("Archived", listed.Status);

        var archivedOnly = (await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>(
            $"{Route}?countryCode=QZ&status=Archived"))!;
        Assert.Contains(archivedOnly.Items, item => item.Id == property.Id);

        using var getById = await client.GetAsync($"{Route}/{property.Id}");
        Assert.Equal(HttpStatusCode.OK, getById.StatusCode);
        Assert.Equal("Archived", (await getById.Content.ReadFromJsonAsync<PropertyResponse>())!.Status);
    }

    [Fact]
    public async Task RealArchiveWithTheServerClockBehindKeepsTheCurrentUpdateInstantExactly()
    {
        // Seeded an hour ahead of the server clock, so the server's UtcNow is behind the stored update instant.
        var property = await SeedPropertyAsync(createdAtOffset: TimeSpan.FromHours(1));
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal("Archived", body.Status);
        Assert.Equal(property.UpdatedAt, body.UpdatedAt);
        Assert.Equal(property.UpdatedAt, (await LoadAsync(property.Id)).UpdatedAt);
    }

    [Fact]
    public async Task UnknownIdReturns404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ArchiveRoute(Guid.NewGuid()), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.not_found", error.Code);
    }

    [Fact]
    public async Task NonGuidIdIsNotRoutedAndReturns404()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsync($"{Route}/not-a-guid/archive", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AlreadyArchivedPropertyReturns409AndIsNotChanged()
    {
        var property = await SeedPropertyAsync(archive: true);
        using var client = AuthorizedClient();

        using var response = await client.PostAsync(ArchiveRoute(property.Id), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.archived", error.Code);
        Assert.Equal(409, error.Status);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(property.UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAfterArchiveReturns409PropertiesArchived()
    {
        var property = await SeedPropertyAsync();
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        using var client = AuthorizedClient();

        using var archive = await client.PostAsync(ArchiveRoute(property.Id), content: null);
        var archived = (await archive.Content.ReadFromJsonAsync<PropertyResponse>())!;
        using var update = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Unit", "AE"));

        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        var error = (await update.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.archived", error.Code);

        var persisted = await LoadAsync(property.Id);
        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(archived.UpdatedAt, persisted.UpdatedAt);
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyArchive);
        return _factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    private static string ArchiveRoute(Guid id) => $"{Route}/{id}/archive";

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
}
