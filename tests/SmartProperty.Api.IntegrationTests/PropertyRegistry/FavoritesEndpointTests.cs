using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Favorites;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Covers a signed-in user's favorite properties against the real host and database: marking, unmarking, listing,
/// and that one user can never see or change another's list.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class FavoritesEndpointTests : IDisposable
{
    private const string Route = "/api/favorites";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public FavoritesEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    // ---- who may call

    [Fact]
    public async Task EveryEndpointRequiresSigningIn()
    {
        using var client = _factory.CreateClient();
        var id = Guid.NewGuid();

        using var list = await client.GetAsync(Route);
        using var ids = await client.GetAsync($"{Route}/ids");
        using var add = await client.PutAsync($"{Route}/{id}", content: null);
        using var remove = await client.DeleteAsync($"{Route}/{id}");

        Assert.All([list, ids, add, remove], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task AnyUserWithoutAnyPermissionCanKeepFavorites()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- marking

    [Fact]
    public async Task MarkingAPropertyReturns201AndStoresIt()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<FavoriteMarkResponse>())!;
        Assert.Equal(propertyId, body.PropertyId);

        await using var context = _fixture.CreateContext();
        var stored = await context.FavoriteProperties.AsNoTracking().SingleAsync(f => f.UserId == userId);
        Assert.Equal(propertyId, stored.PropertyId);
        Assert.InRange((body.FavoritedAt - stored.CreatedAt).Duration(), TimeSpan.Zero, TimeSpan.FromTicks(9));
    }

    [Fact]
    public async Task MarkingAgainReturns200WithTheOriginalInstantAndStoresNothingNew()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        using var first = await client.PutAsync($"{Route}/{propertyId}", content: null);
        var original = (await first.Content.ReadFromJsonAsync<FavoriteMarkResponse>())!;

        using var second = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(original.FavoritedAt, (await second.Content.ReadFromJsonAsync<FavoriteMarkResponse>())!.FavoritedAt);
        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.FavoriteProperties.CountAsync(f => f.UserId == userId));
    }

    [Fact]
    public async Task AnUnknownPropertyReturns404()
    {
        var userId = await SeedUserAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PutAsync($"{Route}/{Guid.NewGuid()}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("properties.not_found", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task AnArchivedPropertyCannotBeAddedButAnExistingFavoriteSurvivesItsArchiving()
    {
        var userId = await SeedUserAsync();
        var kept = await SeedPropertyAsync();
        var archived = await SeedPropertyAsync(archive: true);
        using var client = _factory.CreateAuthenticatedClient(userId);
        using var added = await client.PutAsync($"{Route}/{kept}", content: null);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await ArchiveAsync(kept);

        using var refused = await client.PutAsync($"{Route}/{archived}", content: null);
        using var again = await client.PutAsync($"{Route}/{kept}", content: null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("favorites.property_archived", (await refused.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task ATokenForAUserThatNoLongerExistsReturns404NotAServerError()
    {
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var response = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("users.not_found", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    // ---- unmarking

    [Fact]
    public async Task UnmarkingRemovesItAndReturns204EvenTheSecondTime()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        using var added = await client.PutAsync($"{Route}/{propertyId}", content: null);

        using var first = await client.DeleteAsync($"{Route}/{propertyId}");
        using var second = await client.DeleteAsync($"{Route}/{propertyId}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        await using var context = _fixture.CreateContext();
        Assert.Equal(0, await context.FavoriteProperties.CountAsync(f => f.UserId == userId));
    }

    [Fact]
    public async Task UnmarkingAnUnknownPropertyIsStill204()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.DeleteAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- listing

    [Fact]
    public async Task TheListIsTheUsersOwnNewestFirstWithTheFullPropertyShape()
    {
        var userId = await SeedUserAsync();
        var first = await SeedPropertyAsync();
        var second = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        (await client.PutAsync($"{Route}/{first}", content: null)).Dispose();
        await Task.Delay(20);
        (await client.PutAsync($"{Route}/{second}", content: null)).Dispose();

        var page = (await client.GetFromJsonAsync<PagedResponse<FavoriteResponse>>(Route))!;

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([second, first], page.Items.Select(item => item.Property.Id));
        Assert.True(page.Items[0].FavoritedAt >= page.Items[1].FavoritedAt);
        Assert.Equal("Apartment", page.Items[0].Property.Type);
        Assert.Equal("Active", page.Items[0].Property.Status);
        Assert.NotNull(page.Items[0].Property.AiSync);
        Assert.NotNull(page.Items[0].Property.Amenities);
    }

    [Fact]
    public async Task TheListPagesAndReportsTheWholeTotal()
    {
        var userId = await SeedUserAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        for (var i = 0; i < 3; i++)
        {
            (await client.PutAsync($"{Route}/{await SeedPropertyAsync()}", content: null)).Dispose();
            await Task.Delay(20);
        }

        var first = (await client.GetFromJsonAsync<PagedResponse<FavoriteResponse>>($"{Route}?page=1&pageSize=2"))!;
        var second = (await client.GetFromJsonAsync<PagedResponse<FavoriteResponse>>($"{Route}?page=2&pageSize=2"))!;

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Empty(first.Items.Select(item => item.Property.Id).Intersect(second.Items.Select(item => item.Property.Id)));
    }

    [Fact]
    public async Task AnArchivedPropertyStaysInTheList()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        (await client.PutAsync($"{Route}/{propertyId}", content: null)).Dispose();
        await ArchiveAsync(propertyId);

        var page = (await client.GetFromJsonAsync<PagedResponse<FavoriteResponse>>(Route))!;

        Assert.Equal("Archived", Assert.Single(page.Items).Property.Status);
    }

    [Fact]
    public async Task OneUserNeverSeesOrChangesAnotherUsersFavorites()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync();
        using var aliceClient = _factory.CreateAuthenticatedClient(alice);
        using var bobClient = _factory.CreateAuthenticatedClient(bob);
        (await aliceClient.PutAsync($"{Route}/{propertyId}", content: null)).Dispose();

        var bobsList = (await bobClient.GetFromJsonAsync<PagedResponse<FavoriteResponse>>(Route))!;
        var bobsIds = (await bobClient.GetFromJsonAsync<Guid[]>($"{Route}/ids"))!;
        using var bobRemoves = await bobClient.DeleteAsync($"{Route}/{propertyId}");

        Assert.Empty(bobsList.Items);
        Assert.Empty(bobsIds);
        Assert.Equal(HttpStatusCode.NoContent, bobRemoves.StatusCode);
        var alicesIds = (await aliceClient.GetFromJsonAsync<Guid[]>($"{Route}/ids"))!;
        Assert.Equal([propertyId], alicesIds);
    }

    [Fact]
    public async Task TheIdListHoldsTheUsersFavoritesNewestFirst()
    {
        var userId = await SeedUserAsync();
        var first = await SeedPropertyAsync();
        var second = await SeedPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(userId);
        (await client.PutAsync($"{Route}/{first}", content: null)).Dispose();
        await Task.Delay(20);
        (await client.PutAsync($"{Route}/{second}", content: null)).Dispose();

        var ids = (await client.GetFromJsonAsync<Guid[]>($"{Route}/ids"))!;

        Assert.Equal([second, first], ids);
    }

    [Fact]
    public async Task AUserWithNoFavoritesGetsEmptyListsNotAnError()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        var page = (await client.GetFromJsonAsync<PagedResponse<FavoriteResponse>>(Route))!;
        var ids = (await client.GetFromJsonAsync<Guid[]>($"{Route}/ids"))!;

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(ids);
    }

    // ---- helpers

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Buyer", DateTimeOffset.UtcNow);

        await using var context = _fixture.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedPropertyAsync(bool archive = false)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), createdAt);

        if (archive)
        {
            property.Archive(createdAt.AddMinutes(1));
        }

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task ArchiveAsync(Guid propertyId)
    {
        await using var context = _fixture.CreateContext();
        var property = await context.Properties.Include(p => p.Amenities).Include(p => p.Views)
            .SingleAsync(p => p.Id == propertyId);
        property.Archive(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }
}
