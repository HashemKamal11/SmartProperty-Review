using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyMatching;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.Controllers;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers flow 2 (a user's free-text search, answered by the AI search service and stored as matches) and the two
/// reference endpoints the property form needs: views and the AI service's place list.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AiSearchEndpointTests : IDisposable
{
    private const string SearchRoute = "/api/property-search";
    private const string LocationsRoute = "/api/locations";
    private const string ViewsRoute = "/api/views";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public AiSearchEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    // ---- search

    [Fact]
    public async Task SearchWithoutSigningInIsAnsweredButStoresNothing()
    {
        var ours = await SeedPropertyAsync();
        _factory.AiGateway.SearchOutcome = new AiSearchOutcome(
            "flat", "a flat", 1, [Item(ours.ToString(), "PF-OURS-1", 0.8, "وصف")]);
        await using var before = _fixture.CreateContext();
        var countBefore = await before.PropertyMatches.CountAsync();
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertySearchResponse>())!;
        Assert.Equal(ours, Assert.Single(body.Items).PropertyId);
        Assert.Single(_factory.AiGateway.Searches);
        await using var after = _fixture.CreateContext();
        Assert.Equal(countBefore, await after.PropertyMatches.CountAsync());
    }

    [Fact]
    public async Task EachOfOurResultsCarriesTheSamePropertyObjectGetByIdReturnsAndOthersCarryNone()
    {
        var ours = await SeedPropertyAsync();
        _factory.AiGateway.SearchOutcome = new AiSearchOutcome(
            "flat",
            "a flat",
            2,
            [Item(ours.ToString(), "PF-OURS-1", 0.9, "وصف"), Item(null, "EGY-SALE-APT-007694", 0.4, "someone else's")]);
        using var client = _factory.CreateClient();

        using var search = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat"));
        var body = (await search.Content.ReadFromJsonAsync<PropertySearchResponse>())!;
        var byId = await client.GetStringAsync($"/api/properties/{ours}");

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var mine = body.Items.Single(item => item.PropertyId == ours);
        Assert.NotNull(mine.Property);
        Assert.Equal(ours, mine.Property.Id);
        // The same JSON as the by-id endpoint, field for field.
        Assert.Equal(byId, System.Text.Json.JsonSerializer.Serialize(
            mine.Property,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
        Assert.Null(body.Items.Single(item => item.PropertyId is null).Property);
    }

    [Fact]
    public async Task SearchStoresAMatchForTheSignedInUserAndReturnsEveryResult()
    {
        var userId = await SeedUserAsync();
        var ours = await SeedPropertyAsync();
        _factory.AiGateway.SearchOutcome = new AiSearchOutcome(
            "flat",
            "a flat",
            52600,
            [Item(ours.ToString(), "PF-OURS-1", 0.8765, "وصف عربي"), Item(null, "EGY-SALE-APT-007694", 0.4, "someone else's")]);
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("  flat  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PropertySearchResponse>())!;
        Assert.Equal("flat", body.Query);
        Assert.Equal("a flat", body.CorrectedQuery);
        Assert.Equal(52600, body.TotalAvailable);
        Assert.Equal(2, body.Items.Count);

        var first = body.Items[0];
        Assert.Equal(ours, first.PropertyId);
        Assert.Equal(ours.ToString(), first.ExternalId);
        Assert.Equal("PF-OURS-1", first.AiPropertyId);
        Assert.Equal(87.65m, first.MatchPercentage);
        Assert.Equal("وصف عربي", first.Description);
        Assert.Equal("Cairo", first.Details.City);
        Assert.Equal(4_320_000m, first.Details.Price);

        var other = body.Items[1];
        Assert.Null(other.PropertyId);
        Assert.Null(other.ExternalId);
        Assert.Equal("EGY-SALE-APT-007694", other.AiPropertyId);
        Assert.Equal(40m, other.MatchPercentage);

        var (query, topK) = Assert.Single(_factory.AiGateway.Searches);
        Assert.Equal("flat", query);
        Assert.Equal(3, topK);

        await using var context = _fixture.CreateContext();
        var match = await context.PropertyMatches.AsNoTracking().SingleAsync(row => row.UserId == userId);
        Assert.Equal(ours, match.PropertyId);
        Assert.Equal(87.65m, match.MatchPercentage);
        Assert.Equal("وصف عربي", match.Description);
        Assert.Equal("flat", match.Query);
    }

    [Fact]
    public async Task SearchHonoursTopK()
    {
        var userId = await SeedUserAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat", 7));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, Assert.Single(_factory.AiGateway.Searches).TopK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ABlankQueryReturns422(string? query)
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest(query));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Empty(_factory.AiGateway.Searches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task ATopKOutsideOneToTenReturns422(int topK)
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat", topK));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AnAiOutageReturns502AndStoresNothing()
    {
        var userId = await SeedUserAsync();
        _factory.AiGateway.SearchError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(AiSearchErrors.UnavailableCode, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        await using var context = _fixture.CreateContext();
        Assert.Equal(0, await context.PropertyMatches.CountAsync(row => row.UserId == userId));
    }

    [Fact]
    public async Task ADisabledIntegrationReturns503()
    {
        _factory.AiGateway.SearchError = AiSearchErrors.Disabled;
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PostAsJsonAsync(SearchRoute, new PropertySearchRequest("flat"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AiSearchErrors.DisabledCode, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    // ---- locations

    [Fact]
    public async Task LocationsWithoutSigningInReturn401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(LocationsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StoredCountriesAreListedWithoutCallingTheAiService()
    {
        var country = await SeedCountryAsync();
        _factory.AiGateway.LocationsError = AiSearchErrors.Unavailable("it could not be reached");
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var places = (await client.GetFromJsonAsync<PlaceResponse[]>(LocationsRoute))!;

        Assert.Contains(places, place => place.Id == country.Id && place.NameEn == country.NameEn);
        Assert.Empty(_factory.AiGateway.LocationCalls);
    }

    [Fact]
    public async Task ALevelThatIsNotStoredYetIsFetchedOnceThenServedFromOurTables()
    {
        var country = await SeedCountryAsync();
        var firstAiId = NextAiId();
        var secondAiId = NextAiId();
        _factory.AiGateway.Places = [new AiPlace(firstAiId, "Cairo", "القاهرة"), new AiPlace(secondAiId, "Alexandria", "الإسكندرية")];
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var first = (await client.GetFromJsonAsync<PlaceResponse[]>($"{LocationsRoute}?level=city&parentId={country.Id}"))!;
        var second = (await client.GetFromJsonAsync<PlaceResponse[]>($"{LocationsRoute}?level=city&parentId={country.Id}"))!;

        Assert.Equal(["Alexandria", "Cairo"], first.Select(place => place.NameEn));
        Assert.Equal("القاهرة", first[1].NameAr);
        Assert.Equal(first, second);
        Assert.Equal(("city", (int?)country.AiId), Assert.Single(_factory.AiGateway.LocationCalls));

        // The ids returned are ours, and the rows are stored under the country.
        await using var context = _fixture.CreateContext();
        var stored = await context.Cities.AsNoTracking().Where(city => city.CountryId == country.Id).ToListAsync();
        Assert.Equal(first.Select(place => place.Id).Order(), stored.Select(city => city.Id).Order());
        Assert.Equal(new[] { firstAiId, secondAiId }.Order(), stored.Select(city => city.AiId).Order());
    }

    [Fact]
    public async Task ThePlaceTreeCanBeWalkedFromCountryToCompound()
    {
        var country = await SeedCountryAsync();
        _factory.AiGateway.Places = [new AiPlace(NextAiId(), "Cairo", "القاهرة")];
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var cities = (await client.GetFromJsonAsync<PlaceResponse[]>($"{LocationsRoute}?level=city&parentId={country.Id}"))!;
        _factory.AiGateway.Places = [new AiPlace(NextAiId(), "Helwan", "حلوان")];
        var areas = (await client.GetFromJsonAsync<PlaceResponse[]>($"{LocationsRoute}?level=area&parentId={cities[0].Id}"))!;
        _factory.AiGateway.Places = [new AiPlace(NextAiId(), "Garden Residence", "جاردن ريزيدنس")];
        var compounds = (await client.GetFromJsonAsync<PlaceResponse[]>($"{LocationsRoute}?level=compound&parentId={areas[0].Id}"))!;

        Assert.Equal("Helwan", Assert.Single(areas).NameEn);
        Assert.Equal("Garden Residence", Assert.Single(compounds).NameEn);
        Assert.Equal(3, _factory.AiGateway.LocationCalls.Count);
    }

    [Theory]
    [InlineData("street")]
    [InlineData("1")]
    [InlineData("0")]
    public async Task AnInvalidLevelReturns422(string level)
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{LocationsRoute}?level={level}&parentId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_factory.AiGateway.LocationCalls);
    }

    [Fact]
    public async Task AParentWithoutAnIdReturns422()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{LocationsRoute}?level=city");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_factory.AiGateway.LocationCalls);
    }

    [Fact]
    public async Task AParentThatDoesNotExistReturns404()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{LocationsRoute}?level=city&parentId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("locations.parent_not_found", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Empty(_factory.AiGateway.LocationCalls);
    }

    [Fact]
    public async Task AnAiOutageWhileFillingALevelReturns502AndStoresNothing()
    {
        var country = await SeedCountryAsync();
        _factory.AiGateway.LocationsError = AiSearchErrors.Unavailable("it did not answer in time");
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{LocationsRoute}?level=city&parentId={country.Id}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await using var context = _fixture.CreateContext();
        Assert.Equal(0, await context.Cities.CountAsync(city => city.CountryId == country.Id));
    }

    [Fact]
    public async Task ADisabledIntegrationWhileFillingALevelReturns503()
    {
        var country = await SeedCountryAsync();
        _factory.AiGateway.LocationsError = AiSearchErrors.Disabled;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.GetAsync($"{LocationsRoute}?level=city&parentId={country.Id}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- views

    [Fact]
    public async Task ViewsWithoutSigningInReturn401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(ViewsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnySignedInUserCanListTheEightSeededViewsInDisplayOrder()
    {
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var views = (await client.GetFromJsonAsync<ViewResponse[]>(ViewsRoute))!;

        Assert.Equal(
            ["City", "Community", "Garden", "Golf", "Nile", "Pool", "Sea", "Street"],
            views.Select(view => view.Code));
        Assert.Equal("مدينة", views[0].NameAr);
        Assert.Equal("نيل", views[4].NameAr);
        Assert.Equal("Nile", views[4].NameEn);
    }

    private static AiSearchItem Item(string? externalId, string aiPropertyId, double score, string description)
    {
        return new AiSearchItem(
            aiPropertyId, externalId, score, "Cairo", "القاهرة", "Shorouk", "الشروق", null, null, null, null, "Apartment", "شقة",
            "Sale", "بيع", 4_320_000m, "EGP", 196m, 3, 3, "Finished", false, null, "Security | Parking",
            "Available", null, null, description, null);
    }

    private static int NextAiId()
    {
        // Places of the shared database are unique by AI id, so every test draws its own.
        return Random.Shared.Next(10_000_000, 2_000_000_000);
    }

    private async Task<Country> SeedCountryAsync()
    {
        var country = new Country(Guid.NewGuid(), NextAiId(), "Egypt", "مصر");

        await using var context = _fixture.CreateContext();
        context.Countries.Add(country);
        await context.SaveChangesAsync();

        return country;
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
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }
}
