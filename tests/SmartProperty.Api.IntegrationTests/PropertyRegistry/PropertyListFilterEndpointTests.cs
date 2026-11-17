using System.Net;
using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// How <c>GET /api/properties</c> reads its multi-value and new filters: comma-separated enums are separate values,
/// never their bitwise combination, and the listing without a status returns every status with each item's own.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyListFilterEndpointTests(ApiPostgreSqlFixture fixture)
{
    private readonly AuthScenario scenario = new(fixture);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    [Fact]
    public async Task CommaSeparatedTypesAreEachTypeNotTheirBitwiseCombination()
    {
        var (client, factory, owner) = await OwnerClientAsync();
        using var _ = factory;
        using var __ = client;
        var land = await SeedAsync(owner, PropertyType.Land);
        var building = await SeedAsync(owner, PropertyType.Building);
        await SeedAsync(owner, PropertyType.Apartment); // Land (1) | Building (2) = 3 = Apartment
        var villa = await SeedAsync(owner, PropertyType.Villa);

        Assert.Equal(new[] { land.Id, building.Id }.Order(), (await IdsAsync(client, "?type=Land,Building")).Order());
        Assert.Equal(new[] { land.Id, building.Id }.Order(), (await IdsAsync(client, "?type=Land&type=Building")).Order());
        Assert.Equal(new[] { land.Id, villa.Id }.Order(), (await IdsAsync(client, "?type=land, VILLA")).Order());
        Assert.Equal([villa.Id], await IdsAsync(client, "?type=4"));
    }

    [Theory]
    [InlineData("?type=Land,Castle")]
    [InlineData("?type=99")]
    [InlineData("?type=0")]
    [InlineData("?finishingStatus=Finished,Polished")]
    [InlineData("?furnished=maybe")]
    [InlineData("?cityId=not-a-guid")]
    public async Task AnUnknownValueIsMalformed(string query)
    {
        var (client, factory, owner) = await OwnerClientAsync();
        using var _ = factory;
        using var __ = client;

        using var response = await client.GetAsync("/api/properties" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", await CodeAsync(response));
    }

    [Theory]
    [InlineData("?minBathrooms=4&maxBathrooms=2")]
    [InlineData("?minAreaSqm=300&maxAreaSqm=100")]
    [InlineData("?minAreaSqm=-1")]
    [InlineData("?cityId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?compoundId=00000000-0000-0000-0000-000000000000")]
    public async Task AnInvalidCombinationIsAValidationFailure(string query)
    {
        var (client, factory, owner) = await OwnerClientAsync();
        using var _ = factory;
        using var __ = client;

        using var response = await client.GetAsync("/api/properties" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeAsync(response));
    }

    [Fact]
    public async Task FinishingFurnishedBathroomsAndAreaFilterTheList()
    {
        var (client, factory, owner) = await OwnerClientAsync();
        using var _ = factory;
        using var __ = client;
        var luxury = await SeedAsync(
            owner, PropertyType.Villa, bathrooms: 3, finishing: FinishingStatus.LuxuryFinished,
            physical: new PropertyPhysicalInfo(areaSqm: 250m, furnished: true));
        var semi = await SeedAsync(
            owner, PropertyType.Villa, bathrooms: 1, finishing: FinishingStatus.SemiFinished,
            physical: new PropertyPhysicalInfo(areaSqm: 80m, furnished: false));

        Assert.Equal(new[] { luxury.Id, semi.Id }.Order(), (await IdsAsync(client, "?finishingStatus=LuxuryFinished,SemiFinished")).Order());
        Assert.Equal([luxury.Id], await IdsAsync(client, "?furnished=true"));
        Assert.Equal([semi.Id], await IdsAsync(client, "?furnished=false"));
        Assert.Equal([luxury.Id], await IdsAsync(client, "?minBathrooms=2"));
        Assert.Equal([semi.Id], await IdsAsync(client, "?maxAreaSqm=100"));
    }

    [Fact]
    public async Task WithoutAStatusEveryStatusIsListedAndEachItemCarriesItsOwn()
    {
        var (client, factory, owner) = await OwnerClientAsync();
        using var _ = factory;
        using var __ = client;
        var active = await SeedAsync(owner, PropertyType.Land);
        var archived = await SeedAsync(owner, PropertyType.Land, archived: true);

        var all = await StatusesAsync(client, "");

        Assert.Equal("Active", all[active.Id]);
        Assert.Equal("Archived", all[archived.Id]);
        Assert.Equal([active.Id], (await StatusesAsync(client, "?status=Active")).Keys);
        Assert.Equal([archived.Id], (await StatusesAsync(client, "?status=Archived")).Keys);
    }

    private async Task<(HttpClient Client, SmartPropertyApiFactory Factory, Guid Owner)> OwnerClientAsync()
    {
        var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        var setup = factory.CreateClient();
        var registered = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(registered.UserId, PermissionCodes.PropertyRead);

        return (factory.CreateAuthenticatedClient(registered.UserId), factory, registered.UserId);
    }

    private static async Task<Guid[]> IdsAsync(HttpClient client, string query)
    {
        return (await StatusesAsync(client, query)).Keys.ToArray();
    }

    private static async Task<Dictionary<Guid, string>> StatusesAsync(HttpClient client, string query)
    {
        var separator = query.Length == 0 ? "?" : "&";
        using var response = await client.GetAsync($"/api/properties{query}{separator}pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("id").GetGuid(),
            item => item.GetProperty("status").GetString()!);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("code").GetString();
    }

    private async Task<Property> SeedAsync(
        Guid owner,
        PropertyType type,
        int? bathrooms = null,
        FinishingStatus? finishing = null,
        PropertyPhysicalInfo? physical = null,
        bool archived = false)
    {
        var now = DateTimeOffset.UtcNow;
        var property = new Property(
            Guid.NewGuid(), type, new PropertyAddress("SA"), now,
            bathrooms: bathrooms, finishingStatus: finishing, physical: physical, ownerUserId: owner);

        if (archived)
        {
            property.Archive(now);
        }

        await using var context = fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
