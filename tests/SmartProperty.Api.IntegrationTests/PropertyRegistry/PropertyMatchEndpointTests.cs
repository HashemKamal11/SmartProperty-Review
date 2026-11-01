using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyMatching;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers the endpoint the external AI system calls to store its match results: the shared-key authentication,
/// the validation, and the persisted row.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyMatchEndpointTests : IDisposable
{
    private const string Route = "/api/property-matches";
    private const string ApiKey = "test-ai-integration-key-0123456789";
    private const string KeyHeader = "X-Api-Key";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertyMatchEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(
            fixture,
            configuration: [new KeyValuePair<string, string?>("AiIntegration:ApiKey", ApiKey)]);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task RequestWithoutTheKeyReturns401AndStoresNothing()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(userId, propertyId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountForAsync(userId));
    }

    [Fact]
    public async Task RequestWithAWrongKeyReturns401AndStoresNothing()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = ClientWithKey("not-the-key");

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(userId, propertyId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountForAsync(userId));
    }

    [Fact]
    public async Task AUserJwtIsNotAcceptedInPlaceOfTheKey()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(userId, propertyId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    public async Task WhenNoKeyIsConfiguredEveryRequestReturns401(string suppliedKey)
    {
        var (userId, propertyId) = await SeedAsync();
        using var unconfigured = new SmartPropertyApiFactory(_fixture);
        using var client = unconfigured.CreateClient();
        client.DefaultRequestHeaders.Add(KeyHeader, suppliedKey);

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(userId, propertyId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidRequestReturns201AndPersistsTheMatch()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var response = await client.PostAsJsonAsync(
            Route,
            new RecordPropertyMatchRequest(userId, propertyId, 87.5m, "  Close to the metro.  ", "  2 bed in Nasr City  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertyMatchResponse>())!;
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal(userId, body.UserId);
        Assert.Equal(propertyId, body.PropertyId);
        Assert.Equal(87.5m, body.MatchPercentage);
        Assert.Equal("Close to the metro.", body.Description);
        Assert.Equal("2 bed in Nasr City", body.Query);

        await using var context = _fixture.CreateContext();
        var persisted = await context.PropertyMatches.AsNoTracking().SingleAsync(row => row.Id == body.Id);
        Assert.Equal(userId, persisted.UserId);
        Assert.Equal(propertyId, persisted.PropertyId);
        Assert.Equal(87.5m, persisted.MatchPercentage);
        Assert.Equal("Close to the metro.", persisted.Description);
        Assert.Equal("2 bed in Nasr City", persisted.Query);
    }

    [Fact]
    public async Task ThreeMatchesForOneSearchAreAllStored()
    {
        var user = await SeedUserAsync();
        var first = await SeedPropertyAsync();
        var second = await SeedPropertyAsync();
        var third = await SeedPropertyAsync();
        using var client = ClientWithKey(ApiKey);

        foreach (var (propertyId, percentage) in new[] { (first, 91m), (second, 78m), (third, 64m) })
        {
            using var response = await client.PostAsJsonAsync(Route, ValidRequest(user, propertyId, percentage));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(3, await CountForAsync(user));
    }

    [Fact]
    public async Task AnUnknownUserReturns404AndStoresNothing()
    {
        var (_, propertyId) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(Guid.NewGuid(), propertyId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("users.not_found", error.Code);
    }

    [Fact]
    public async Task AnUnknownPropertyReturns404AndStoresNothing()
    {
        var (userId, _) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var response = await client.PostAsJsonAsync(Route, ValidRequest(userId, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("properties.not_found", error.Code);
        Assert.Equal(0, await CountForAsync(userId));
    }

    [Fact]
    public async Task MissingUserIdOrPropertyIdReturns422()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var noUser = await client.PostAsJsonAsync(
            Route, new RecordPropertyMatchRequest(null, propertyId, 50m, "text", "query"));
        using var noProperty = await client.PostAsJsonAsync(
            Route, new RecordPropertyMatchRequest(userId, null, 50m, "text", "query"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noUser.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProperty.StatusCode);
    }

    [Theory]
    [InlineData(null, "text", "query")]
    [InlineData(-0.5, "text", "query")]
    [InlineData(100.5, "text", "query")]
    [InlineData(50.0, null, "query")]
    [InlineData(50.0, "   ", "query")]
    [InlineData(50.0, "text", null)]
    [InlineData(50.0, "text", "   ")]
    public async Task InvalidBodyReturns422AndStoresNothing(
        double? percentage,
        string? description,
        string? query)
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var response = await client.PostAsJsonAsync(
            Route,
            new RecordPropertyMatchRequest(userId, propertyId, (decimal?)percentage, description, query));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Equal(0, await CountForAsync(userId));
    }

    [Fact]
    public async Task OverlongDescriptionOrQueryReturns422()
    {
        var (userId, propertyId) = await SeedAsync();
        using var client = ClientWithKey(ApiKey);

        using var longDescription = await client.PostAsJsonAsync(
            Route, new RecordPropertyMatchRequest(userId, propertyId, 50m, new string('d', 4001), "query"));
        using var longQuery = await client.PostAsJsonAsync(
            Route, new RecordPropertyMatchRequest(userId, propertyId, 50m, "text", new string('q', 2001)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, longDescription.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longQuery.StatusCode);
    }

    [Fact]
    public async Task AMalformedBodyReturns400()
    {
        using var client = ClientWithKey(ApiKey);
        using var content = new StringContent("{\"userId\": \"not-a-guid\"}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Route, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient ClientWithKey(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(KeyHeader, key);
        return client;
    }

    private static RecordPropertyMatchRequest ValidRequest(Guid userId, Guid propertyId, decimal percentage = 80m)
    {
        return new RecordPropertyMatchRequest(userId, propertyId, percentage, "Good fit.", "flat in cairo");
    }

    private async Task<(Guid UserId, Guid PropertyId)> SeedAsync()
    {
        return (await SeedUserAsync(), await SeedPropertyAsync());
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Buyer", DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedPropertyAsync()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task<int> CountForAsync(Guid userId)
    {
        await using var context = _fixture.CreateContext();
        return await context.PropertyMatches.CountAsync(row => row.UserId == userId);
    }
}
