using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// A property is the physical asset only. Prices, the transaction type, availability and promotional text belong to
/// marketplace listings, so the property endpoints neither accept nor return them, and a client that still sends one
/// is told so instead of losing it silently.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyPhysicalEndpointTests : IDisposable
{
    private const string Route = "/api/properties";

    private static readonly string[] CommercialMembers =
        ["transactionType", "price", "listingStatus", "titleAr", "titleEn", "descriptionAr", "descriptionEn", "aiSync"];

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public PropertyPhysicalEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateReturnsThePhysicalFactsAndNoCommercialMember()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            new CreatePropertyRequest("Penthouse", TestPlaces.Seed(_fixture).AreaId, AreaSqm: 240m, Floor: 12, Furnished: true, TimeZoneId: "Africa/Cairo"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var physical = json.RootElement.GetProperty("physical");
        Assert.Equal(240m, physical.GetProperty("areaSqm").GetDecimal());
        Assert.Equal(12, physical.GetProperty("floor").GetInt32());
        Assert.True(physical.GetProperty("furnished").GetBoolean());
        Assert.Equal("Africa/Cairo", physical.GetProperty("timeZoneId").GetString());
        AssertNoCommercialMember(json.RootElement);
        Assert.False(json.RootElement.TryGetProperty("listing", out _));
    }

    [Fact]
    public async Task ReadingAPropertyReturnsNoCommercialMember()
    {
        using var client = AuthorizedClient();
        using var created = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", TestPlaces.Seed(_fixture).AreaId));
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;

        using var one = await client.GetAsync($"{Route}/{id}");
        using var list = await client.GetAsync(Route);

        using var oneJson = JsonDocument.Parse(await one.Content.ReadAsStringAsync());
        AssertNoCommercialMember(oneJson.RootElement);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        foreach (var item in listJson.RootElement.GetProperty("items").EnumerateArray())
        {
            AssertNoCommercialMember(item);
        }
    }

    [Theory]
    [InlineData("transactionType", "\"Sale\"")]
    [InlineData("price", "4500000")]
    [InlineData("listingStatus", "\"Available\"")]
    [InlineData("titleEn", "\"A villa\"")]
    [InlineData("descriptionAr", "\"وصف\"")]
    public async Task CreateRefusesACommercialMemberInsteadOfDroppingItSilently(string member, string value)
    {
        using var client = AuthorizedClient();
        var area = TestPlaces.Seed(_fixture).AreaId;
        using var content = new StringContent(
            $"{{\"type\":\"Land\",\"areaId\":\"{area}\",\"{member}\":{value}}}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Route, content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal("validation.failed", error.Code);
        Assert.Contains(member, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateRefusesACommercialMemberAndChangesNothing()
    {
        using var client = AuthorizedClient();
        var area = TestPlaces.Seed(_fixture).AreaId;
        using var created = await client.PostAsJsonAsync(Route, new CreatePropertyRequest("Land", area, AreaSqm: 100m));
        var id = (await created.Content.ReadFromJsonAsync<CreatePropertyResponse>())!.Id;
        using var content = new StringContent(
            $"{{\"type\":\"Land\",\"areaId\":\"{area}\",\"areaSqm\":999,\"price\":1000}}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PutAsync($"{Route}/{id}", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var stored = (await client.GetFromJsonAsync<PropertyResponse>($"{Route}/{id}"))!;
        Assert.Equal(100m, stored.Physical!.AreaSqm);
    }

    [Fact]
    public async Task TheOldListFiltersAreNoLongerPartOfTheContract()
    {
        using var client = AuthorizedClient();

        // Unknown query keys are ignored like any other, so these cannot filter and must not fail.
        using var response = await client.GetAsync($"{Route}?minPrice=1&maxPrice=2&listingStatus=Sold&sort=Newest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheSortByPriceIsRefusedBecausePriceIsNotAPropertyFact()
    {
        using var client = AuthorizedClient();

        using var response = await client.GetAsync($"{Route}?sort=PriceAsc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ThePropertyLevelAiSyncEndpointIsGone()
    {
        using var client = AuthorizedClient();

        using var response = await client.PostAsync($"{Route}/{Guid.NewGuid()}/ai-sync", content: null);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    private static void AssertNoCommercialMember(JsonElement element)
    {
        foreach (var member in CommercialMembers)
        {
            Assert.False(element.TryGetProperty(member, out _), $"'{member}' must not be part of a property.");
        }
    }

    private HttpClient AuthorizedClient()
    {
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyCreate);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyRead);
        _factory.PermissionChecker.AllowedPermissionCodes.Add(PermissionCodes.PropertyUpdate);
        return _factory.CreateAuthenticatedClient(TestUsers.Seed(_fixture));
    }
}
