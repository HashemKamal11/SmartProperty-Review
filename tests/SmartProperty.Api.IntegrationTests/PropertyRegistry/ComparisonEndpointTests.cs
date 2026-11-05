using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.PropertyComparison;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// A user's comparison list against the real host and database: adding up to three properties of one kind, reading the
/// comparison with the smart-pick match, and that one user can never see or change another's list.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class ComparisonEndpointTests : IDisposable
{
    private const string Route = "/api/comparison";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;

    public ComparisonEndpointTests(ApiPostgreSqlFixture fixture)
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

        var responses = new[]
        {
            await client.GetAsync(Route),
            await client.PutAsync($"{Route}/{id}", content: null),
            await client.DeleteAsync($"{Route}/{id}"),
            await client.DeleteAsync(Route),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task AnyUserWithoutAnyPermissionCanCompare()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync(TransactionType.Sale);
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- adding and reading

    [Fact]
    public async Task ANewUserHasAnEmptyListThatCannotBeCompared()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        var list = (await client.GetFromJsonAsync<PropertyComparisonResponse>(Route))!;

        Assert.Empty(list.Items);
        Assert.False(list.CanCompare);
        Assert.Equal(3, list.MaxItems);
        Assert.Equal(2, list.MinItemsToCompare);
        Assert.Null(list.TransactionType);
    }

    [Fact]
    public async Task AddingReturns201WithTheWholeListAndStoresIt()
    {
        var userId = await SeedUserAsync();
        var propertyId = await SeedPropertyAsync(TransactionType.Sale, price: 4_850_000m);
        using var client = _factory.CreateAuthenticatedClient(userId);

        using var response = await client.PutAsync($"{Route}/{propertyId}", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<PropertyComparisonResponse>())!;
        var item = Assert.Single(list.Items);
        Assert.Equal(propertyId, item.Property.Id);
        Assert.Equal(4_850_000m, item.Property.Listing!.Price);
        Assert.Equal("Sale", list.TransactionType);
        Assert.False(list.CanCompare);

        await using var context = _fixture.CreateContext();
        var stored = await context.PropertyComparisonItems.AsNoTracking().SingleAsync(row => row.UserId == userId);
        Assert.Equal(propertyId, stored.PropertyId);
        Assert.Equal(1, stored.Position);
    }

    [Fact]
    public async Task TwoPropertiesCanBeComparedAndAgainAddingTheSameOneIs200()
    {
        var userId = await SeedUserAsync();
        var first = await SeedPropertyAsync(TransactionType.Rent);
        var second = await SeedPropertyAsync(TransactionType.Rent);
        using var client = _factory.CreateAuthenticatedClient(userId);
        (await client.PutAsync($"{Route}/{first}", content: null)).Dispose();

        using var added = await client.PutAsync($"{Route}/{second}", content: null);
        using var again = await client.PutAsync($"{Route}/{second}", content: null);

        var list = (await added.Content.ReadFromJsonAsync<PropertyComparisonResponse>())!;
        Assert.True(list.CanCompare);
        Assert.Equal([first, second], list.Items.Select(item => item.Property.Id));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, (await again.Content.ReadFromJsonAsync<PropertyComparisonResponse>())!.Items.Count);
    }

    // ---- the rules

    [Fact]
    public async Task AFourthPropertyIs409()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        for (var index = 0; index < 3; index++)
        {
            (await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Sale)}", content: null)).Dispose();
        }

        using var response = await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Sale)}", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("comparison.limit_reached", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task ASaleCannotBeComparedWithARent()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Sale)}", content: null)).Dispose();

        using var response = await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Rent)}", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "comparison.transaction_type_mismatch",
            (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task APropertyForNeitherSaleNorRentIs409()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());

        using var response = await client.PutAsync($"{Route}/{await SeedPropertyAsync(transactionType: null)}", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("comparison.property_not_listed", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task AnUnknownPropertyIs404AndAnArchivedOneIs409()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        var archived = await SeedPropertyAsync(TransactionType.Sale, archive: true);

        using var unknown = await client.PutAsync($"{Route}/{Guid.NewGuid()}", content: null);
        using var gone = await client.PutAsync($"{Route}/{archived}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Equal("comparison.property_archived", (await gone.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    // ---- the smart-pick match

    [Fact]
    public async Task TheMatchShowsWhenTheAiGaveOneAndIsNullWhenItDidNot()
    {
        var userId = await SeedUserAsync();
        var matched = await SeedPropertyAsync(TransactionType.Sale);
        var unmatched = await SeedPropertyAsync(TransactionType.Sale);
        await StoreMatchAsync(userId, matched, 70m, DateTimeOffset.UtcNow.AddHours(-2));
        await StoreMatchAsync(userId, matched, 96m, DateTimeOffset.UtcNow.AddHours(-1));
        using var client = _factory.CreateAuthenticatedClient(userId);
        (await client.PutAsync($"{Route}/{matched}", content: null)).Dispose();
        (await client.PutAsync($"{Route}/{unmatched}", content: null)).Dispose();

        var list = (await client.GetFromJsonAsync<PropertyComparisonResponse>(Route))!;

        var withMatch = list.Items.Single(item => item.Property.Id == matched);
        Assert.Equal(96m, withMatch.MatchPercentage);
        Assert.Equal("Matches the need", withMatch.MatchDescription);
        var without = list.Items.Single(item => item.Property.Id == unmatched);
        Assert.Null(without.MatchPercentage);
        Assert.Null(without.MatchDescription);
    }

    // ---- removing and isolation

    [Fact]
    public async Task RemovingReturnsTheRemainingListAndRemovingAgainIsFine()
    {
        var first = await SeedPropertyAsync(TransactionType.Sale);
        var second = await SeedPropertyAsync(TransactionType.Sale);
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await client.PutAsync($"{Route}/{first}", content: null)).Dispose();
        (await client.PutAsync($"{Route}/{second}", content: null)).Dispose();

        using var removed = await client.DeleteAsync($"{Route}/{first}");
        using var again = await client.DeleteAsync($"{Route}/{first}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var remaining = (await removed.Content.ReadFromJsonAsync<PropertyComparisonResponse>())!;
        Assert.Equal(second, Assert.Single(remaining.Items).Property.Id);
        Assert.False(remaining.CanCompare);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task ClearingEmptiesTheListAndFreesTheKindAgain()
    {
        using var client = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Sale)}", content: null)).Dispose();

        using var cleared = await client.DeleteAsync(Route);
        using var rent = await client.PutAsync($"{Route}/{await SeedPropertyAsync(TransactionType.Rent)}", content: null);

        Assert.Empty((await cleared.Content.ReadFromJsonAsync<PropertyComparisonResponse>())!.Items);
        Assert.Equal(HttpStatusCode.Created, rent.StatusCode);
    }

    [Fact]
    public async Task OneUserNeverSeesOrChangesAnothersList()
    {
        var propertyId = await SeedPropertyAsync(TransactionType.Sale);
        using var mine = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        using var theirs = _factory.CreateAuthenticatedClient(await SeedUserAsync());
        (await mine.PutAsync($"{Route}/{propertyId}", content: null)).Dispose();

        var theirList = (await theirs.GetFromJsonAsync<PropertyComparisonResponse>(Route))!;
        (await theirs.DeleteAsync($"{Route}/{propertyId}")).Dispose();
        (await theirs.DeleteAsync(Route)).Dispose();
        var myList = (await mine.GetFromJsonAsync<PropertyComparisonResponse>(Route))!;

        Assert.Empty(theirList.Items);
        Assert.Single(myList.Items);
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

    private async Task<Guid> SeedPropertyAsync(
        TransactionType? transactionType,
        decimal price = 2_000_000m,
        bool archive = false)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var listing = transactionType is null
            ? null
            : new PropertyListingInfo(transactionType, price, 150m, titleEn: "A villa");
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("SA"),
            createdAt,
            listing: listing);

        if (archive)
        {
            property.Archive(createdAt.AddMinutes(1));
        }

        await using var context = _fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task StoreMatchAsync(Guid userId, Guid propertyId, decimal percentage, DateTimeOffset createdAt)
    {
        await using var context = _fixture.CreateContext();
        context.PropertyMatches.Add(new PropertyMatch(
            Guid.NewGuid(), userId, propertyId, percentage, "Matches the need", "a villa in Riyadh", createdAt));
        await context.SaveChangesAsync();
    }
}
