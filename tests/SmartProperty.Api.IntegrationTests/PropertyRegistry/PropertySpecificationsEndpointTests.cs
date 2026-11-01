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
/// Covers bedrooms, bathrooms, built date, and finishing status across the create, update, get, and list
/// endpoints against the real host and database.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertySpecificationsEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertySpecificationsEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateReturnsAndPersistsEverySpecification()
    {
        using var client = AuthorizedClient();
        var request = new CreatePropertyRequest(
            "Unit",
            "EG",
            Bedrooms: 3,
            Bathrooms: 2,
            BuiltDate: new DateOnly(2021, 6, 30),
            FinishingStatus: "SuperLux");

        using var response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Equal(3, body.Bedrooms);
        Assert.Equal(2, body.Bathrooms);
        Assert.Equal(new DateOnly(2021, 6, 30), body.BuiltDate);
        Assert.Equal("SuperLux", body.FinishingStatus);

        var persisted = await LoadAsync(body.Id);
        Assert.Equal(3, persisted.Bedrooms);
        Assert.Equal(2, persisted.Bathrooms);
        Assert.Equal(new DateOnly(2021, 6, 30), persisted.BuiltDate);
        Assert.Equal(FinishingStatus.SuperLux, persisted.FinishingStatus);
    }

    [Fact]
    public async Task CreateWithoutSpecificationsReturnsThemAsNull()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", "EG"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatePropertyResponse>())!;
        Assert.Null(body.Bedrooms);
        Assert.Null(body.Bathrooms);
        Assert.Null(body.BuiltDate);
        Assert.Null(body.FinishingStatus);
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(101, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, 101, null)]
    [InlineData(null, null, "Shiny")]
    [InlineData(null, null, "furnished")]
    public async Task CreateRejectsInvalidSpecificationsWith422AndStoresNothing(
        int? bedrooms,
        int? bathrooms,
        string? finishingStatus)
    {
        using var client = AuthorizedClient();
        var countBefore = await CountAsync();

        using var response = await client.PostAsJsonAsync(
            Route,
            new CreatePropertyRequest(
                "Unit", "EG", Bedrooms: bedrooms, Bathrooms: bathrooms, FinishingStatus: finishingStatus));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(countBefore, await CountAsync());
    }

    [Fact]
    public async Task CreateRejectsABuiltDateBeforeTheEarliestSupported()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            new CreatePropertyRequest("Unit", "EG", BuiltDate: new DateOnly(1799, 12, 31)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReplacesSpecificationsAndOmittingThemClearsThem()
    {
        var property = await SeedAsync();
        using var client = AuthorizedClient();

        using var set = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest(
                "Unit", "EG", Bedrooms: 4, Bathrooms: 3, BuiltDate: new DateOnly(2015, 1, 2), FinishingStatus: "Furnished"));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var updated = (await set.Content.ReadFromJsonAsync<PropertyResponse>())!;
        Assert.Equal(4, updated.Bedrooms);
        Assert.Equal(3, updated.Bathrooms);
        Assert.Equal(new DateOnly(2015, 1, 2), updated.BuiltDate);
        Assert.Equal("Furnished", updated.FinishingStatus);

        using var cleared = await client.PutAsJsonAsync($"{Route}/{property.Id}", new UpdatePropertyRequest("Unit", "EG"));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var persisted = await LoadAsync(property.Id);
        Assert.Null(persisted.Bedrooms);
        Assert.Null(persisted.Bathrooms);
        Assert.Null(persisted.BuiltDate);
        Assert.Null(persisted.FinishingStatus);
    }

    [Fact]
    public async Task UpdateRejectsAnInvalidFinishingStatusAndChangesNothing()
    {
        var property = await SeedAsync(bedrooms: 2);
        using var client = AuthorizedClient();

        using var response = await client.PutAsJsonAsync(
            $"{Route}/{property.Id}",
            new UpdatePropertyRequest("Unit", "EG", Bedrooms: 5, FinishingStatus: "Shiny"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(2, (await LoadAsync(property.Id)).Bedrooms);
    }

    [Fact]
    public async Task GetByIdAndListReturnTheSpecifications()
    {
        var property = await SeedAsync(
            bedrooms: 2, bathrooms: 1, builtDate: new DateOnly(2010, 9, 9), finishingStatus: FinishingStatus.SemiFinished);
        using var client = AuthorizedClient();

        var single = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{property.Id}"))!;
        Assert.Equal(2, single.Bedrooms);
        Assert.Equal(1, single.Bathrooms);
        Assert.Equal(new DateOnly(2010, 9, 9), single.BuiltDate);
        Assert.Equal("SemiFinished", single.FinishingStatus);

        var page = (await client.GetFromJsonAsync<PagedResponse<PropertyResponse>>($"{Route}?pageSize=100"))!;
        var listed = Assert.Single(page.Items, item => item.Id == property.Id);
        Assert.Equal(2, listed.Bedrooms);
        Assert.Equal("SemiFinished", listed.FinishingStatus);
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    private async Task<Property> SeedAsync(
        int? bedrooms = null,
        int? bathrooms = null,
        DateOnly? builtDate = null,
        FinishingStatus? finishingStatus = null)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero).AddMinutes(-10);
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), createdAt, bedrooms, bathrooms, builtDate, finishingStatus);

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

    private async Task<int> CountAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.Properties.CountAsync();
    }
}
